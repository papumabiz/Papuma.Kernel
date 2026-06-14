// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

namespace EventModeledSlices.Features.PlaceOrder;

/// <summary>The command DTO — invariant shape, fields from the model.</summary>
public sealed record PlaceOrder(string ProductId, int Quantity);
