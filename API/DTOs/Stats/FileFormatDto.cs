﻿using API.Entities.Enums;

namespace API.DTOs.Stats;

public class FileFormatDto
{
    /// <summary>
    /// The extension with the ., in lowercase
    /// </summary>
    public string Extension { get; set; }
    /// <summary>
    /// Format of extension
    /// </summary>
    public MangaFormat Format { get; set; }
}
