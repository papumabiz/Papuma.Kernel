// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

namespace Papuma.Kernel.FSharp

open Papuma.Kernel.Store

/// <summary>
/// The kernel's <em>expected</em> write-time outcomes (ADR-003/ADR-006), as a closed set
/// instead of the C# side's exception types — F# convention prefers <c>Result</c> for
/// outcomes the caller is meant to branch on, not just catch. Deliberately narrow: only
/// <see cref="ConcurrencyException"/>, <see cref="DocumentNotFoundException"/> and
/// <see cref="UniqueKeyViolationException"/> are represented here, because those three are
/// the ones ADR-003/ADR-006 document as routine, expected write outcomes an application is
/// meant to handle. Anything else (<c>SchemaUpcastRequiredException</c>, infra failures,
/// programmer errors) is not — those stay real .NET exceptions and propagate unchanged,
/// same as they do in C#. This mirrors, not replaces, the C# API: <c>Papuma.Kernel</c> and
/// <c>Papuma.Kernel.Local</c> keep throwing exactly as documented; nothing here reaches
/// back into the kernel.
/// </summary>
type KernelError =
    /// The stored version didn't match `expectedVersion` at write time.
    | VersionConflict of documentType: string * documentId: string * expected: int64 * actual: int64
    /// An update or delete targeted a document that doesn't exist in the current scope.
    | DocumentNotFound of documentType: string * documentId: string
    /// A declared unique key was violated by this write.
    | UniqueKeyViolation of documentType: string * keyPath: string

/// <summary>
/// Classifies the three expected kernel exceptions into <see cref="KernelError"/>. Public
/// (not <c>internal</c>) because <see cref="Session.trySaveAsync"/>/<c>tryPatchAsync</c>
/// are <c>inline</c> — an SRTP requirement — and inline functions get expanded at the call
/// site in the consumer's own assembly, which can't see internal members of this one.
/// </summary>
module KernelErrorClassifier =

    let (|Known|_|) (ex: exn) : KernelError option =
        match ex with
        | :? ConcurrencyException as e -> Some(VersionConflict(e.DocumentType, e.DocumentId, e.ExpectedVersion, e.ActualVersion))
        | :? DocumentNotFoundException as e -> Some(DocumentNotFound(e.DocumentType, e.DocumentId))
        | :? UniqueKeyViolationException as e -> Some(UniqueKeyViolation(e.DocumentType, e.KeyPath))
        | _ -> None
