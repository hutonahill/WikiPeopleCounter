using System.Diagnostics;
using System.Reflection;
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
    
    public const string UserAgentString = "WikiPeopleCounter/0.1 (https://github.com/hutonahill/WikiPeopleCounter)";
    
    private static ProgressBar? _bar;
    
    private static async Task Main(string[] args) {
        SetUp();
        
        foreach (string category in _categoriesToSearch) {
            string? lastSortKey;
            using (PageDataContext ctx = new ()) {
                lastSortKey = ctx.Categories
                   .FirstOrDefault(c => c.Title == category)?
                   .LastSortKey;
            }
            
            await FetchCategoryPagesAsync(category, lastSortKey);
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
        
        foreach (string catigory in CategoriesToSearch) {
            if (!context.Categories.Any(c => c.Title == catigory)) {
                context.Categories.Add(new Category(catigory));
            }
        }
        
        int totalPages = context.Pages.Count();
        int processedPages = context.Pages.Count(p => p.Processed);
        int searchedCatigoryCount = context.Categories.Count(c => c.Finished);
        
        if (totalPages == 0) {
            Console.WriteLine("Database is empty. Ready to start fetching pages.");
            _categoriesToSearch = context.Categories.Select(c => c.Title).ToList();
        }
        else {
            // DB is not empty
            if (Question("Database contains pages. Do you want to wipe all pages?")) {
                context.Pages.RemoveRange(context.Pages);
                context.SaveChanges();
                Console.WriteLine("Database wiped. Ready to start fresh.");
                _categoriesToSearch = CategoriesToSearch;
                context.Categories.ForEachAsync(c => c.Finished = false);
            }
            else {
                if (processedPages == 0) {
                    Console.WriteLine("Database has pages, but none have been processed.");
                    
                    if (Question("Do you want to search for more pages?")) {
                        if (searchedCatigoryCount > 0 && Question("Some categories have already been fully searched. Do you want to re-search them?")) {
                            _categoriesToSearch = context.Categories.Select(c => c.Title).ToList();
                        }
                        else {
                            _categoriesToSearch = context.Categories
                               .Where(c => c.Finished == false)
                               .Select(c => c.Title)
                               .ToList();
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
    
    private static async Task<bool> FetchCategoryPagesAsync(string category, string? startingSortKey = null) {
        string cmcontinue = string.Empty;
        int batchIndex = 1;

        await using PageDataContext context = new();

        Category target = context.Categories.FirstOrDefault(c => c.Title == category) ??
                          throw new InvalidOperationException("Cannot search for a non-existent category");
    
        while (true) {
            Dictionary<string, string> parameters = new() {
                { "action", "query" },
                { "format", "json" },
                { "list", "categorymembers" },
                { "cmtitle", category },
                { "cmtype", "page" },
                { "cmlimit", "max" },
                { "cmsort", "sortkey" },
                { "cmprop", "title|ids|sortkey" }
            };

            if (!string.IsNullOrEmpty(cmcontinue)) {
                parameters["cmcontinue"] = cmcontinue;
            }
            else if (!string.IsNullOrEmpty(startingSortKey)) {
                parameters["cmstarthexsortkey"] = startingSortKey;
            }

            using JsonDocument doc = await QueryWikipediaApiAsync(parameters);
            JsonElement root = doc.RootElement;

            if (!root.TryGetProperty("query", out JsonElement queryElement) ||
                !queryElement.TryGetProperty("categorymembers", out JsonElement pagesElement)) 
            {
                Console.WriteLine($"No pages found in category '{category}' or unexpected response structure.");
                return false;
            }

            int addedCount = 0;
            HashSet<Page> newPages = new();

            foreach (JsonElement page in pagesElement.EnumerateArray()) {
                string title = page.GetProperty("title").GetString() 
                               ?? throw new InvalidDataException("Expected a title property.");
                
                // name removed disambiguation data.
                string name = Regex.Replace(title, @"\s*\(.*?\)$", "");
                
                
                string wikiId = page.TryGetProperty("pageid", out JsonElement idProp)
                    ? idProp.GetInt32().ToString()
                    : throw new InvalidDataException("Expected a pageid property.");

                string sortKey = page.TryGetProperty("sortkey", out JsonElement sortKeyProp)
                    ? sortKeyProp.GetString() ?? throw new InvalidDataException("Expected a sortkey property.")
                    : throw new InvalidDataException("Expected a sortkey property.");
                
                // check pages added in the current batch.
                if (newPages.Any(p => p.Title == title || p.Name == name || p.WikiPageId == wikiId)) {
                    continue;
                }
                
                // then check the database.
                if (await context.Pages.AnyAsync(p => p.Title == title || p.Name == name || p.WikiPageId == wikiId)) {
                    continue;
                }
                
                // assuming both tests pass, we add the new page.
                Page newPage = new() {
                    Title = title,
                    Name = name,
                    WikiPageId = wikiId,
                    SortKey = sortKey,
                    PulledFrom = target
                };

                newPages.Add(newPage);
                addedCount++;
                
                // once a batch is done we update the last seen sortkey in the database.
                // This will prevent us from needing to walk the whole db again in the case of a crash.
                // We only call saveChanges once per batch, so we don't need to worry about updates.
                target.LastSortKey = newPage.SortKey;
            }

            if (newPages.Any()) {
                await context.Pages.AddRangeAsync(newPages);
                
                

                await context.SaveChangesAsync();
            }

            Console.WriteLine($"Fetched {addedCount:N0} new pages from batch #{batchIndex:N0}. Total in DB: {context.Pages.Count():N0}");
            batchIndex++;

            // Check if we are done
            if (
                root.TryGetProperty("continue", out JsonElement cont) &&
                cont.TryGetProperty("cmcontinue", out JsonElement contVal) &&
                !string.IsNullOrEmpty(contVal.GetString())
            ) {
                cmcontinue = contVal.GetString()!;
            }
            else {
                // No more continuation → mark category as finished
                target.Finished = true;
                await context.SaveChangesAsync();
                Console.WriteLine($"Category '{category}' fully fetched.");
                break;
            }
        }

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

        ProgressBarOptions options = new() { ForegroundColor = ConsoleColor.Cyan };
        
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

    private static async Task<Dictionary<string, int>> GetBacklinksBatchAsync(List<string> titles) {
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
                { "prop", "linkshere" },
                { "titles", joinedTitles },
                { "lhlimit", "max" }
            };

            if (!string.IsNullOrEmpty(lhContinue)) {
                parameters["lhcontinue"] = lhContinue;
            }
            
            using JsonDocument doc = await QueryWikipediaApiAsync(parameters);
            
            if (doc.RootElement.TryGetProperty("query", out JsonElement queryEl) &&
                queryEl.TryGetProperty("pages", out JsonElement pages)) {
                foreach (JsonProperty pageProp in pages.EnumerateObject()) {
                    JsonElement page = pageProp.Value;
                    
                    if (page.TryGetProperty("title", out JsonElement titleEl) &&
                        page.TryGetProperty("linkshere", out JsonElement links)) {
                        string pageTitle = titleEl.GetString() ?? string.Empty;
                        results[pageTitle] += links.GetArrayLength();
                    }
                }
            }
            
            if (doc.RootElement.TryGetProperty("continue", out JsonElement cont) &&
                cont.TryGetProperty("lhcontinue", out JsonElement contVal)) {
                lhContinue = contVal.GetString() ?? string.Empty;
            }
            else {
                lhContinue = string.Empty;
            }
            

        } while (!string.IsNullOrEmpty(lhContinue));

        return results;
    }

    private static async Task<Dictionary<string, (string? Url, int? Length, int? Translations)>> GetPageInfoBatchAsync(
        List<string> titles
    ) {
        Dictionary<string, (string? Url, int? Length, int? Translations)> results = new ();

        foreach (string title in titles) {
            results[title] = (null, null, 0);
        }

        string llContinue = string.Empty;
        string joinedTitles = string.Join("|", titles.Select(t => t.Replace(" ", "_")));

        do {
            Dictionary<string, string> parameters = new () {
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

            using JsonDocument doc = await QueryWikipediaApiAsync(parameters);

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
            
            

        } while (!string.IsNullOrEmpty(llContinue));

        return results;
    }

    private static async Task<bool> ProcessPageBatchAsync(
        TimeSpan pageviewsPeriod,
        int batchSize = 500,
        int delayMilliseconds = 500)
    {

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
        Dictionary<string, int> backlinks = await GetBacklinksBatchAsync(titleList);
        Dictionary<string, (string? Url, int? Length, int? Translations)> pageInfo = 
            await GetPageInfoBatchAsync(titleList);

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
            
            page.LastUpdated = DateTime.Now;
            
            await context.SaveChangesAsync();
            _bar?.Tick($"Processed '{page.Title}'");
        }

        return await context.Pages.AnyAsync(p => !p.Processed);
    }
    
    private static readonly HttpClient _client = new ();
    private static int _minimumQueryDelayMilliseconds = 700;
    
    private static async Task<JsonDocument> QueryWikipediaApiAsync(
        Dictionary<string, string> parameters)
    {
        const string baseUrl = "https://en.wikipedia.org/w/api.php";
        const int maxRetries = 10;
        

        // Ensure required parameters
        parameters.TryAdd("format", "json");
        
        const string maxlagString = "maxlag";
        const int maxLagTime = 5;
        
        int timeoutDelay = 10000;
        
        parameters.TryAdd(maxlagString, maxLagTime.ToString());

        // Ensure User-Agent exists
        if (!_client.DefaultRequestHeaders.UserAgent.Any()) {
            _client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgentString);
        }

        int attempt = 0;
        
        while (attempt < maxRetries) {
            attempt++;

            
            // Enforce minimum delay between requests
            TimeSpan wait =
                (_lastQueryTime + TimeSpan.FromMilliseconds(_minimumQueryDelayMilliseconds))
                - DateTime.UtcNow;

            if (wait > TimeSpan.Zero) {
                await Task.Delay(wait);
            }

            string query = string.Join("&",
                parameters.Select(kvp =>
                    kvp.Key + "=" + Uri.EscapeDataString(kvp.Value)));

            string url = baseUrl + "?" + query;
            
            try {
                HttpResponseMessage response = await _client.GetAsync(url);
                _lastQueryTime = DateTime.UtcNow;

                // Detect HTTP rate limiting
                if ((int)response.StatusCode == 429 ||
                    response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable)
                {
                    Console.WriteLine($"Wikipedia rate limiting detected. Incrementing minimum wait time by 0.1 second and waiting {timeoutDelay/1000f:N1} seconds...");
                    _minimumQueryDelayMilliseconds += 100;
                    await Task.Delay(timeoutDelay);
                    timeoutDelay += timeoutDelay;
                }
                else if (!response.IsSuccessStatusCode) {
                    throw new Exception("Wikipedia returned HTTP " + response.StatusCode);
                }
                else {
                    string raw = await response.Content.ReadAsStringAsync();
                    JsonDocument doc = JsonDocument.Parse(raw);

                    // Detect maxlag error
                    if (doc.RootElement.TryGetProperty("error", out JsonElement errorElement)) {
                        if (errorElement.TryGetProperty("code", out JsonElement codeElement)) {
                            string? code = codeElement.GetString();

                            if (code == maxlagString) {
                                int waitMS = maxLagTime;

                                if (errorElement.TryGetProperty("lag", out JsonElement lagElement)) {
                                    waitMS = (lagElement.GetInt32()*1000) + 100;
                                }

                                Console.WriteLine($"Wikipedia maxlag hit. Waiting {(waitMS/1000)} seconds...");

                                await Task.Delay(waitMS);
                            }
                            else {
                                Console.WriteLine("Wikipedia API error: " + code);
                                return doc;
                            }
                        }
                    }
                    else {
                        return doc;
                    }
                }
            }
            catch (Exception ex) {
                Console.WriteLine("Wikipedia request exception: " + ex.Message);
                await Task.Delay(2000);
            }
        }
        
        throw new Exception($"Wikipedia request failed after the maximum number of retries ({maxRetries}).");
    }
}