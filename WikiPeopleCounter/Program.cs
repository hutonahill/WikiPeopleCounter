using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using ShellProgressBar;
using WikiPeopleCounter.Data;
using WikiPeopleCounter.Models;

namespace WikiPeopleCounter;

class Program {
    public static readonly IReadOnlyList<string> CategoriesToSearch = new List<string> {
        "Category:Living people"
    };
    
    public static readonly KeyValuePair<string, string> DefaultHeader = new ("User-Agent", "WikiPeopleCrawler/1.0");
    
    private static ProgressBar? _bar;
    
    private static async Task Main(string[] args) {
        SetUp();
        
        foreach (string category in _categoriesToSearch) {
            if (await FetchCategoryPagesAsync(category)) {
                await using (PageDataContext ctx = new ()) {
                    ctx.CategoriesSearched.Add(new CategoriesSearched(category));
                    await ctx.SaveChangesAsync();
                }
            }
        }
        
        /*int count = -1;
        using (PageDataContext ctx = new ()) {
            count = ctx.Pages.Count(p => !p.Processed);
        }
        
        _bar = new ProgressBar(count, "Processing pages");
        
        bool running = true;
        while (running) {
            running = await ProcessPageBatchAsync(TimeSpan.FromDays(30));
        }
        
        _bar.Dispose();*/
    }
    
    private static readonly IReadOnlyDictionary<string, bool> _translator = new Dictionary<string, bool> {
        { "y", true },
        { "yes", true },
        { "t", true },
        { "true", true },
        { "1", true },
        { "on", true },
        
        { "n", false },
        { "no", false },
        { "f", false },
        { "false", false },
        { "0", false },
        { "off", false }
    };
    
    private static bool Question(string prompt) {
        while (true) {
            Console.Write($"{prompt} [y/n]: ");
            string? input = Console.ReadLine()?.Trim().ToLower();
            
            if (input != null && _translator.TryGetValue(input, out bool question)) {
                return question;
            }
            else {
                Console.WriteLine("Please input 'y' or 'n'.");
            }
        }
    }
    
    private static void SetUp() {
        using var context = new PageDataContext();
        // Ensure database and tables exist
        context.Database.EnsureCreated();
        
        int totalPages = context.Pages.Count();
        int processedPages = context.Pages.Count(p => p.Processed);
        int totalCategories = context.CategoriesSearched.Count();
        
        if (totalPages == 0) {
            Console.WriteLine("Database is empty. Ready to start fetching pages.");
            _categoriesToSearch = CategoriesToSearch;
        }
        else {
            // DB is not empty
            if (Question("Database contains pages. Do you want to wipe all pages?")) {
                context.Pages.RemoveRange(context.Pages);
                context.SaveChanges();
                Console.WriteLine("Database wiped. Ready to start fresh.");
                _categoriesToSearch = CategoriesToSearch;
            }
            else {
                if (processedPages == 0) {
                    Console.WriteLine("Database has pages, but none have been processed.");
                    
                    if (Question("Do you want to search for more pages?")) {
                        if (totalCategories > 0 && Question("Some categories have already been fully searched. Do you want to re-search them?")) {
                            _categoriesToSearch = CategoriesToSearch;
                        }
                        else {
                            _categoriesToSearch = CategoriesToSearch
                               .Where(c => !context.CategoriesSearched
                                   .Any(cat => cat.Title == c)
                               ).ToList();
                        }
                    }
                    else {
                        Console.WriteLine("No new pages will be fetched. Continuing with existing pages.");
                    }
                }
                else {
                    Console.WriteLine($"Database has processed {processedPages:N0}/{totalPages:N0} pages. Continuing processing.");
                }
            }
        }
    }
    
    // Global variable to hold categories chosen by the user
    private static IReadOnlyList<string> _categoriesToSearch = new List<string>();
    
    private static DateTime _lastQueryTime = DateTime.MinValue;
    
    private static int i = 1;
    
    private static async Task<bool> FetchCategoryPagesAsync(string category) {
        const int batchSize = 5000; // I think this is media-wiki's max.
        const double delayMS = 800;

        using HttpClient client = new ();
        client.DefaultRequestHeaders.Add(DefaultHeader.Key, DefaultHeader.Value);

        string cmcontinue = string.Empty;
        
        await using PageDataContext context = new ();
        
        string url = $"https://en.wikipedia.org/w/api.php";
        
        while (true) {
            Dictionary<string, string> parameters = new() {
                { "action", "query" },
                { "format", "json" },
                { "list", "categorymembers" },          // ← switch to list
                { "cmtitle", category },
                { "cmtype", "page" },
                { "cmlimit", batchSize.ToString() }
            };
            
            if (!string.IsNullOrEmpty(cmcontinue)){
                parameters["cmcontinue"] = cmcontinue;
            }
                

            // Build query string
            string query = string.Join("&", parameters.Select(kvp => $"{kvp.Key}={Uri.EscapeDataString(kvp.Value)}"));
            string requestUrl = $"{url}?{query}";

            HttpResponseMessage response;
            try {
                response = await client.GetAsync(requestUrl);
            }
            catch (HttpRequestException ex) {
                Console.WriteLine($"Network error fetching category '{category}': {ex.Message}");
                return false;
            }
            
            _lastQueryTime = DateTime.Now;
            

            if (!response.IsSuccessStatusCode) {
                Console.WriteLine($"HTTP error {response.StatusCode} fetching category '{category}'.");
                return false;
            }

            string rawJson = await response.Content.ReadAsStringAsync();

            JsonDocument doc;
            try {
                doc = JsonDocument.Parse(rawJson);
            }
            catch (JsonException ex) {
                Console.WriteLine($"JSON parse error fetching category '{category}': {ex.Message}");
                return false;
            }

            JsonElement root = doc.RootElement;

            if (!root.TryGetProperty("query", out JsonElement queryElement) || 
                !queryElement.TryGetProperty("categorymembers", out JsonElement pagesElement))
            {
                Console.WriteLine($"No pages found in category '{category}' or unexpected response structure.");
                doc.Dispose();
                return false;
            }

            int addedCount = 0;
            
            HashSet<Page> newPages = new ();
            
            foreach (JsonElement page in pagesElement.EnumerateArray()) {
                string title = page.GetProperty("title").GetString() ??
                               throw new InvalidDataException("Expected a title property.");
                
                // Generate Name by removing " (<anyText>)" from the end
                string name = Regex.Replace(title, @"\s*\(.*?\)$", "");
                
                string wikiId = page.TryGetProperty("pageid", out JsonElement idProp)
                    ? idProp.GetInt32().ToString()
                    : throw new InvalidDataException("Expected a pageid property.");
                
                // Avoid duplicates by Title or Name
               if (await context.Pages.AnyAsync(p => p.Title == title || p.Name == name || p.WikiPageId == wikiId)) {
                    continue;
               }
               
               if (newPages.Any(p => p.Title == title || p.Name == name || p.WikiPageId == wikiId)) {
                   continue;
               }
                
                Page newPage = new() {
                    Title = title,
                    Name = name,
                    WikiPageId = wikiId
                };
                
                newPages.Add(newPage);
                addedCount++;
            }
            
            
            if (newPages.Any()) {
                await context.Pages.AddRangeAsync(newPages);
                            
                await context.SaveChangesAsync();
            }
            Console.WriteLine($"Fetched {addedCount:N0} new pages from batch #{i:N0}. (total {context.Pages.Count():N0})");
            i++;

            // Check for continuation
            if(root.TryGetProperty("continue", out JsonElement cont)){
                if(cont.TryGetProperty("cmcontinue", out JsonElement contVal)){
                    cmcontinue = contVal.GetString() ?? String.Empty;
                }
                else{
                    cmcontinue = String.Empty; // done
                }
            }
            
            doc.Dispose();
            
            // Maintain the minimum amount of time between queries
            TimeSpan actualDelay = (_lastQueryTime + TimeSpan.FromMilliseconds(delayMS)) - DateTime.Now;
            
            if (actualDelay < TimeSpan.Zero) {
                actualDelay = TimeSpan.Zero;
            }
            
            await Task.Delay(actualDelay);
        }

        Console.WriteLine($"Completed fetching all pages from category '{category}'.");
        return true;
    }
    
    private static async Task<Dictionary<string, int>> GetPageViewsBatchAsync(
        HttpClient client,
        IEnumerable<string> titles,
        TimeSpan pageviewsPeriod,
        int delayMilliseconds) // optional parent bar
    {
        DateTime endDate = DateTime.Now.Date;
        DateTime startDate = endDate - pageviewsPeriod;
        Dictionary<string, int> results = new();

        ProgressBarOptions options = new ProgressBarOptions { ForegroundColor = ConsoleColor.Cyan };
        
        IEnumerable<string> enumerable = titles.ToList();
        
        // If parent bar is passed, create a child; else just create a new top-level bar
        Debug.Assert(_bar != null, nameof(_bar) + " != null");
        IProgressBar childBar = _bar.Spawn(enumerable.Count(), "Fetching pageviews...", options);
        
        foreach (string title in enumerable) {
            string encodedTitle = Uri.EscapeDataString(title.Replace(" ", "_"));

            string url = $"https://wikimedia.org/api/rest_v1/metrics/pageviews/per-article/en.wikipedia.org/all-access/user/{encodedTitle}/daily/{startDate:yyyyMMdd}/{endDate:yyyyMMdd}";

            TimeSpan remainingDelay = (_lastQueryTime + TimeSpan.FromMilliseconds(delayMilliseconds)) - DateTime.Now;
            if (remainingDelay > TimeSpan.Zero) await Task.Delay(remainingDelay);

            int totalViews = 0;
            try {
                HttpResponseMessage response = await client.GetAsync(url);
                _lastQueryTime = DateTime.Now;

                if (response.IsSuccessStatusCode) {
                    string raw = await response.Content.ReadAsStringAsync();
                    using JsonDocument doc = JsonDocument.Parse(raw);

                    if (doc.RootElement.TryGetProperty("items", out JsonElement items)) {
                        foreach (JsonElement item in items.EnumerateArray()) {
                            totalViews += item.GetProperty("views").GetInt32();
                        }
                    }
                }
            }
            catch (Exception ex) {
                Console.WriteLine($"Error fetching pageviews for '{title}': {ex.Message}");
            }

            results[title] = totalViews;
            childBar.Tick($"Got view count for '{title}'"); // update child/sub bar
        }
        
        childBar.Dispose();
        return results;
    }

    private static async Task<Dictionary<string, int>> GetBacklinksBatchAsync(
        HttpClient client,
        List<string> titles,
        int delayMilliseconds)
    {
        Dictionary<string, int> results = new ();
        string lhContinue = string.Empty;

        // Initialize counts
        foreach (string title in titles) {
            results[title] = 0;
        }

        string joinedTitles = string.Join("|", titles.Select(t => t.Replace(" ", "_")));

        do {
            Dictionary<string, string> parameters = new () {
                { "action", "query" },
                { "format", "json" },
                { "prop", "linkshere" },
                { "titles", joinedTitles },
                { "lhlimit", "max" }
            };

            if (!string.IsNullOrEmpty(lhContinue)) {
                parameters["lhcontinue"] = lhContinue;
            }

            string query = string.Join("&", parameters.Select(kvp =>
                kvp.Key + "=" + Uri.EscapeDataString(kvp.Value)));

            string url = "https://en.wikipedia.org/w/api.php?" + query;

            TimeSpan remainingDelay =
                _lastQueryTime + TimeSpan.FromMilliseconds(delayMilliseconds) - DateTime.Now;

            if (remainingDelay > TimeSpan.Zero) {
                await Task.Delay(remainingDelay);
            }

            try {
                HttpResponseMessage response = await client.GetAsync(url);
                _lastQueryTime = DateTime.Now;

                if (response.IsSuccessStatusCode) {
                    string raw = await response.Content.ReadAsStringAsync();
                    using JsonDocument doc = JsonDocument.Parse(raw);

                    if (doc.RootElement.TryGetProperty("query", out JsonElement queryEl) &&
                        queryEl.TryGetProperty("pages", out JsonElement pages))
                    {
                        foreach (JsonProperty pageProp in pages.EnumerateObject()) {
                            JsonElement page = pageProp.Value;

                            if (page.TryGetProperty("title", out JsonElement titleEl) &&
                                page.TryGetProperty("linkshere", out JsonElement links))
                            {
                                string pageTitle = titleEl.GetString() ?? string.Empty;
                                results[pageTitle] += links.GetArrayLength();
                            }
                        }
                    }

                    if (doc.RootElement.TryGetProperty("continue", out JsonElement cont) &&
                        cont.TryGetProperty("lhcontinue", out JsonElement contVal))
                    {
                        lhContinue = contVal.GetString() ?? string.Empty;
                    }
                    else {
                        lhContinue = string.Empty;
                    }
                }
                else {
                    lhContinue = string.Empty;
                }
            }
            catch (Exception ex) {
                Console.WriteLine("Error fetching backlinks batch: " + ex.Message);
                lhContinue = string.Empty;
            }

        } while (!string.IsNullOrEmpty(lhContinue));

        return results;
    }

    private static async Task<Dictionary<string, (string? Url, int? Length, int? Translations)>> GetPageInfoBatchAsync(
            HttpClient client,
            List<string> titles,
            int delayMilliseconds)
    {
        Dictionary<string, (string? Url, int? Length, int? Translations)> results = new ();

        foreach (string title in titles) {
            results[title] = (null, null, 0);
        }

        string llContinue = string.Empty;
        string joinedTitles = string.Join("|", titles.Select(t => t.Replace(" ", "_")));

        do {
            Dictionary<string, string> parameters = new Dictionary<string, string>() {
                { "action", "query" },
                { "format", "json" },
                { "titles", joinedTitles },
                { "prop", "info|langlinks" },
                { "inprop", "url" },
                { "lllimit", "max" }
            };

            if (!string.IsNullOrEmpty(llContinue)) {
                parameters["llcontinue"] = llContinue;
            }

            string query = string.Join("&", parameters.Select(kvp =>
                kvp.Key + "=" + Uri.EscapeDataString(kvp.Value)));

            string url = "https://en.wikipedia.org/w/api.php?" + query;

            TimeSpan remainingDelay =
                _lastQueryTime + TimeSpan.FromMilliseconds(delayMilliseconds) - DateTime.Now;

            if (remainingDelay > TimeSpan.Zero) {
                await Task.Delay(remainingDelay);
            }

            try {
                HttpResponseMessage response = await client.GetAsync(url);
                _lastQueryTime = DateTime.Now;

                if (response.IsSuccessStatusCode) {
                    string raw = await response.Content.ReadAsStringAsync();
                    using JsonDocument doc = JsonDocument.Parse(raw);

                    if (doc.RootElement.TryGetProperty("query", out JsonElement queryEl) &&
                        queryEl.TryGetProperty("pages", out JsonElement pagesEl))
                    {
                        foreach (JsonProperty pageProp in pagesEl.EnumerateObject()) {
                            JsonElement page = pageProp.Value;

                            string pageTitle =
                                page.TryGetProperty("title", out JsonElement titleProp)
                                    ? titleProp.GetString() ?? string.Empty
                                    : string.Empty;

                            string? fullUrl =
                                page.TryGetProperty("fullurl", out JsonElement urlProp)
                                    ? urlProp.GetString()
                                    : null;

                            int? length =
                                page.TryGetProperty("length", out JsonElement lenProp)
                                    ? lenProp.GetInt32()
                                    : null;

                            int translationsToAdd = 0;

                            if (page.TryGetProperty("langlinks", out JsonElement langProp)) {
                                translationsToAdd = langProp.GetArrayLength();
                            }

                            if (results.ContainsKey(pageTitle)) {
                                (string? existingUrl, int? existingLength, int? existingTranslations) =
                                    results[pageTitle];

                                int totalTranslations =
                                    (existingTranslations ?? 0) + translationsToAdd;

                                results[pageTitle] =
                                    (fullUrl ?? existingUrl, length ?? existingLength, totalTranslations);
                            }
                        }
                    }

                    if (doc.RootElement.TryGetProperty("continue", out JsonElement cont) &&
                        cont.TryGetProperty("llcontinue", out JsonElement contVal))
                    {
                        llContinue = contVal.GetString() ?? string.Empty;
                    }
                    else {
                        llContinue = string.Empty;
                    }
                }
                else {
                    llContinue = string.Empty;
                }
            }
            catch (Exception ex) {
                Console.WriteLine("Error fetching page info batch: " + ex.Message);
                llContinue = string.Empty;
            }

        } while (!string.IsNullOrEmpty(llContinue));

        return results;
    }

    private static async Task<bool> ProcessPageBatchAsync(
        TimeSpan pageviewsPeriod,
        int batchSize = 500,
        int delayMilliseconds = 500)
    {
        using HttpClient client = new();
        client.DefaultRequestHeaders.Add(DefaultHeader.Key, DefaultHeader.Value);

        await using PageDataContext context = new();

        List<Page> pagesToProcess = await context.Pages
            .Where(p => !p.Processed)
            .OrderBy(p => p.PageId)
            .Take(batchSize)
            .ToListAsync();

        if (pagesToProcess.Count == 0) {
            return false;
        }
        
        List<string> titleList = pagesToProcess
           .Select(p => p.Title)
           .ToList();
        
        //Dictionary<string, int> pageViews = await GetPageViewsBatchAsync(client, titleList, pageviewsPeriod, delayMilliseconds);
        Dictionary<string, int> backlinks = await GetBacklinksBatchAsync(client, titleList, delayMilliseconds);
        Dictionary<string, (string? Url, int? Length, int? Translations)> pageInfo = 
            await GetPageInfoBatchAsync(client, titleList, delayMilliseconds);

        foreach (Page page in pagesToProcess) {
            //page.Views = pageViews[page.Title];
            
            page.Backlinks = backlinks[page.Title];
            
            (string? url, int? length, int? translations) = pageInfo[page.Title];

            if (url != null) {
                page.Url = url;
            }

            if (length.HasValue) {
                page.WordCount = length.Value;
            }

            if (translations.HasValue) {
                page.Translations = translations.Value;
            }
            
            page.Processed = true;
            page.LastUpdated = DateTime.Now;
            
            await context.SaveChangesAsync();
            _bar?.Tick($"Processed '{page.Title}'");
        }

        return await context.Pages.AnyAsync(p => !p.Processed);
    }
    
    private static void Log(string message) {
        
    }
}