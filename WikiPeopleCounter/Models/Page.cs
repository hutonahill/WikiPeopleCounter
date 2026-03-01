// -----------------------------------------------------------------------------
// Project: ${PROJECT_NAME}
// Copyright (c) 2026
// Author: Evan RIker
// GitHub Account: hutonahill
// Email: evan.k.riker@gmail.com
// 
// License: GNU General Public License
// 
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License version 3 as published by
// the Free Software Foundation.
// 
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
// 
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.
// -----------------------------------------------------------------------------

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Diagnostics;

namespace WikiPeopleCounter.Models;

public class Page {
    [Key]
    public uint PageId { get; set; }
    
    public string WikiPageId { get; set; }
    
    public string Title { get; set; }
    
    public string Name { get; set; }
    
    public bool Processed { get; set; } = false;
    
    public int? Translations { get; set; }
        
    public int? WordCount { get; set; }
    
    public string? Url { get; set; }
    
    public int? Views { get; set; }
    
    public int? Backlinks { get; set; }
    
    public DateTime? LastUpdated { get; set; }
    
    public ProcessedPage ToProcessed => new (this);
}

[NotMapped]
public class ProcessedPage {
    public ProcessedPage(Page page) {
        source = page ?? throw new ArgumentNullException(nameof(page));
        
        // Validate all required fields at construction
        if (string.IsNullOrEmpty(source.Title))
            throw new UnreachableException("Title must not be null or empty.");
        
        if (source.Url is null)
            throw new UnreachableException("Url must not be null.");
        
        if (source.Views is null)
            throw new UnreachableException("Views must not be null.");
        
        if (source.Backlinks is null)
            throw new UnreachableException("Backlinks must not be null.");
        
        if (source.Translations is null)
            throw new UnreachableException("Translations must not be null.");
        
        if (source.WordCount is null)
            throw new UnreachableException("WordCount must not be null.");
        
        if (source.LastUpdated is null)
            throw new UnreachableException("LastUpdated must not be null.");
    }
    
    private readonly Page source;
    
    public uint PageId => source.PageId;
    
    public string Title => source.Title;
    
    public string WikiPageId => source.WikiPageId;
    
    public string Name => source.Name;
    
    public string Url => source.Url ?? throw new UnreachableException("source should have been validated.");
    
    public int Views => source.Views ?? throw new UnreachableException("source should have been validated.");
    
    public int Backlinks => source.Backlinks ?? throw new UnreachableException("source should have been validated.");
    
    public int Translations => source.Translations ?? throw new UnreachableException("source should have been validated.");
    
    public int WordCount => source.WordCount ?? throw new UnreachableException("source should have been validated.");
    
    public DateTime LastUpdated =>
        source.LastUpdated ?? throw new UnreachableException("source should have been validated.");
}