// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

namespace EventModeledSlices.Features.PlaceOrder;

/// <summary>The command DTO — invariant shape, fields from the model.</summary>
public sealed record PlaceOrder(string ProductId, int Quantity);
