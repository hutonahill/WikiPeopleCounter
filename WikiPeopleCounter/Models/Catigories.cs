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

using System.ComponentModel.DataAnnotations;

namespace WikiPeopleCounter.Data;

public class CategoriesSearched {
    public CategoriesSearched(string title) {
        Title = title;
    }
    
    [Key] 
    public uint CategoryId { get; set; }
    
    public string Title { get; set; }
}