﻿using System;

namespace API.Entities.Interfaces;

public interface IEntityDate
{
    DateTime Created { get; set; }
    DateTime LastModified { get; set; }
}
