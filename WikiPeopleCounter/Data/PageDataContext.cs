// -----------------------------------------------------------------------------
// Project: WikiPeopleCounter
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

using Microsoft.EntityFrameworkCore;
using WikiPeopleCounter.Models;

namespace WikiPeopleCounter.Data;

public class PageDataContext : DbContext {
    public DbSet<Page> Pages { get; set; }
    public DbSet<CategoriesSearched> CategoriesSearched { get; set; }
    
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) {
        // Build the path to store the SQLite DB in the same folder as the executable
        string exeFolder = AppContext.BaseDirectory;
        string dbPath = Path.Combine(exeFolder, "wiki_pages.db");
        
        optionsBuilder.UseSqlite($"Data Source={dbPath}");
    }
    
    protected override void OnModelCreating(ModelBuilder modelBuilder) {
        base.OnModelCreating(modelBuilder);
        
        // Configure Page table
        modelBuilder.Entity<Page>(entity => {
            entity.HasKey(e => e.PageId);
            
            entity.Property(e => e.Title)
               .IsRequired();
            
            entity.HasIndex(e => e.Title)
               .IsUnique();
            
            entity.HasIndex(e => e.WikiPageId)
               .IsUnique();
            
            entity.HasIndex(e => e.Name)
               .IsUnique();
        });
        
        modelBuilder.Entity<CategoriesSearched>(entity => {
            entity.HasKey(e => e.CategoryId);
            
            entity.Property(e => e.Title)
               .IsRequired();
            
            entity.HasIndex(e => e.Title)
               .IsUnique();
        });
    }
}