// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Text.RegularExpressions;

namespace Papuma.Kernel.Processing;

/// <summary>
/// The rules for a feed handler's <c>Name</c>. The name is the handler's identity in the shared
/// <c>checkpoint</c> and <c>failure</c> tables, where the event feed stores its handlers as
/// <c>event:&lt;name&gt;</c> — so a name must never contain <c>:</c> (a change handler called
/// <c>event:orders</c> would share one cursor with the event handler <c>orders</c>), and must
/// not be empty or null (review P-6, Q-3).
/// </summary>
internal static class FeedHandlerNames
{
    /// <summary>The longest handler name.</summary>
    public const int MaxLength = 100;

    private static readonly Regex Pattern = new(
        @"^[A-Za-z0-9][A-Za-z0-9._-]*\z",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    /// <summary>Validates one handler name.</summary>
    /// <param name="name">The name as the handler reports it.</param>
    /// <param name="handlerType">The handler's type, for the message.</param>
    /// <param name="paramName">The parameter to blame — the constructor's handler list.</param>
    /// <exception cref="ArgumentException">The name is missing, too long or has characters outside <c>A–Z a–z 0–9 . _ -</c>.</exception>
    public static void Validate(string? name, Type handlerType, string paramName)
    {
        ArgumentNullException.ThrowIfNull(handlerType);

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException(
                $"The feed handler {handlerType.Name} has no name. A handler's name is its checkpoint identity.",
                paramName);
        }

        if (name.Length > MaxLength)
        {
            throw new ArgumentException(
                $"The name of feed handler {handlerType.Name} is {name.Length} characters long; at most {MaxLength} are allowed.",
                paramName);
        }

        if (!Pattern.IsMatch(name))
        {
            throw new ArgumentException(
                $"The name '{name}' of feed handler {handlerType.Name} is not valid: use letters, digits, '.', '_' and '-', " +
                "starting with a letter or digit. ':' is reserved — the event feed stores its checkpoints as 'event:<name>'.",
                paramName);
        }
    }
}
