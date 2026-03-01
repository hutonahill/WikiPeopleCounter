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
        using PageDataContext context = new PageDataContext();
        
        // Ensure database and tables exist
        context.Database.EnsureCreated();
        
        // Ensure all target categories exist in the DB
        foreach (string category in CategoriesToSearch) {
            if (!context.Categories.Any(c => c.Title == category)) {
                context.Categories.Add(new Category(category));
            }
        }
        
        context.SaveChanges();
        
        int totalPages = context.Pages.Count();
        int searchedCategoryCount = context.Categories.Count(c => c.Finished);
        
        if (totalPages == 0) {
            Console.WriteLine("Database is empty. Ready to start fetching pages.");
            _categoriesToSearch = context.Categories.Select(c => c.Title).ToList();
        }
        else {
            // DB is not empty
            if (Question("Database contains pages. Do you want to wipe all pages?")) {
                context.Pages.RemoveRange(context.Pages);
                context.Categories.ToList().ForEach(c => c.Finished = false); // reset category status
                context.SaveChanges();
                
                Console.WriteLine("Database wiped. Ready to start fresh.");
                _categoriesToSearch = CategoriesToSearch.ToList();
            }
            else {
                if (searchedCategoryCount > 0 && Question("Some categories have already been fully fetched. Do you want to re-fetch them?")) {
                    _categoriesToSearch = context.Categories.Select(c => c.Title).ToList();
                }
                else {
                    _categoriesToSearch = context.Categories
                       .Where(c => !c.Finished)
                       .Select(c => c.Title)
                       .ToList();
                }
                
                Console.WriteLine($"Database has {totalPages:N0} pages. {_categoriesToSearch.Count:N0} categories will be fetched.");
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