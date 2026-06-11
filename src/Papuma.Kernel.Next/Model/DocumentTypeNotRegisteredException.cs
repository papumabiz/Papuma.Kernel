// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

namespace Papuma.Kernel.Model;

/// <summary>
/// Thrown when a document type is used that was not registered in the kernel model.
/// </summary>
public sealed class DocumentTypeNotRegisteredException : InvalidOperationException
{
    /// <summary>Gets the unregistered CLR type.</summary>
    public Type ClrType { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="DocumentTypeNotRegisteredException"/> class.
    /// </summary>
    /// <param name="clrType">The unregistered CLR type.</param>
    public DocumentTypeNotRegisteredException(Type clrType)
        : base($"Document type {clrType.FullName} is not registered. " +
               "Register it via KernelModelBuilder.Document<T>() before opening sessions.")
    {
        ClrType = clrType;
    }
}
