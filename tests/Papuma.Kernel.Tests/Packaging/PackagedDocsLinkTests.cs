// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Papuma.Kernel.Tests.Packaging;

/// <summary>
/// Guards the doc set every package ships (the <c>PackagedDocs</c> item group in
/// <c>Directory.Build.targets</c>): a relative link must resolve inside that set, and
/// its anchor must name an existing heading. Package READMEs are rendered by nuget.org,
/// which resolves no relative link at all, so they must link absolutely.
/// </summary>
public sealed class PackagedDocsLinkTests
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    // [text](target) and [text](target#anchor); images share the syntax.
    private static readonly Regex MarkdownLink = new(
        @"\]\((?<target>[^)\s#]*)(?:#(?<anchor>[^)\s]*))?\)",
        RegexOptions.Compiled, RegexTimeout);

    private static readonly Regex HtmlSource = new(
        @"<(?:img|a)\b[^>]*?\b(?:src|href)=""(?<target>[^""]+)""",
        RegexOptions.Compiled, RegexTimeout);

    private static readonly Regex Heading = new(
        @"^#{1,6}\s+(?<text>.+?)\s*$",
        RegexOptions.Compiled, RegexTimeout);

    private static readonly Regex Scheme = new(
        "^[a-z][a-z0-9+.-]*:",
        RegexOptions.Compiled | RegexOptions.IgnoreCase, RegexTimeout);

    private static readonly string RepoRoot = FindRepoRoot();

    [Fact]
    public void PackagedDocs_RelativeLinksResolveInsideThePackage()
    {
        var shipped = LoadPackagedDocs();
        Assert.NotEmpty(shipped);

        var failures = new List<string>();
        foreach (var file in shipped.Order(StringComparer.Ordinal))
        {
            var content = File.ReadAllText(Path.Combine(RepoRoot, file));
            foreach (Match link in MarkdownLink.Matches(content))
            {
                var target = link.Groups["target"].Value;
                var anchor = link.Groups["anchor"].Success ? link.Groups["anchor"].Value : null;
                if (Scheme.IsMatch(target))
                {
                    continue;
                }

                var resolved = target.Length == 0 ? file : Resolve(file, target);
                var isFile = shipped.Contains(resolved);
                var isFolder = shipped.Any(s => s.StartsWith(resolved + "/", StringComparison.Ordinal));
                if (!isFile && !isFolder)
                {
                    failures.Add($"{file}: '{target}' is not shipped — link it absolutely");
                }
                else if (anchor is not null && isFile && !HeadingSlugs(resolved).Contains(anchor))
                {
                    failures.Add($"{file}: '{target}#{anchor}' names no heading");
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void PackageReadmes_LinkOnlyAbsolutely()
    {
        var readmes = LoadPackageReadmes();
        Assert.NotEmpty(readmes);

        var failures = new List<string>();
        foreach (var readme in readmes)
        {
            var content = File.ReadAllText(Path.Combine(RepoRoot, readme));
            var targets = MarkdownLink.Matches(content).Select(m => m.Groups["target"].Value)
                .Concat(HtmlSource.Matches(content).Select(m => m.Groups["target"].Value));
            failures.AddRange(targets
                .Where(t => t.Length > 0 && !Scheme.IsMatch(t))
                .Select(t => $"{readme}: '{t}' is relative — nuget.org cannot resolve it"));
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static HashSet<string> LoadPackagedDocs()
    {
        var targets = XDocument.Load(Path.Combine(RepoRoot, "Directory.Build.targets"));
        var group = targets.Root!.Elements("ItemGroup")
            .Single(g => (string?)g.Attribute("Label") == "PackagedDocs");

        var shipped = new HashSet<string>(StringComparer.Ordinal);
        foreach (var include in group.Elements("None").Select(e => (string)e.Attribute("Include")!))
        {
            // Only the "<folder>/*.md" shape is used; anything else should fail loudly here.
            var relative = include.Replace("$(MSBuildThisFileDirectory)", string.Empty, StringComparison.Ordinal);
            Assert.EndsWith("/*.md", relative);
            var folder = Path.Combine(RepoRoot, relative[..^"/*.md".Length]);
            foreach (var path in Directory.GetFiles(folder, "*.md"))
            {
                shipped.Add(ToRepoPath(path));
            }
        }

        return shipped;
    }

    private static List<string> LoadPackageReadmes()
    {
        var projects = Directory.GetFiles(Path.Combine(RepoRoot, "src"), "*.*proj", SearchOption.AllDirectories);
        var readmes = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var project in projects)
        {
            var document = XDocument.Load(project);
            var readmeName = document.Descendants("PackageReadmeFile").SingleOrDefault()?.Value;
            if (readmeName is null)
            {
                continue;
            }

            // MSBuild paths use '\' on every OS; Linux does not treat it as a separator.
            var include = document.Descendants("None")
                .Select(e => ((string?)e.Attribute("Include"))?.Replace('\\', '/'))
                .Single(i => i is not null && Path.GetFileName(i) == readmeName)!;
            readmes.Add(ToRepoPath(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(project)!, include))));
        }

        return [.. readmes];
    }

    /// <summary>GitHub's heading anchors: lowercase, punctuation dropped, spaces to dashes, duplicates numbered.</summary>
    private static HashSet<string> HeadingSlugs(string file)
    {
        var slugs = new HashSet<string>(StringComparer.Ordinal);
        var inFence = false;
        foreach (var line in File.ReadLines(Path.Combine(RepoRoot, file)))
        {
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }

            var heading = inFence ? null : Heading.Match(line);
            if (heading is not { Success: true })
            {
                continue;
            }

            var slug = new StringBuilder();
            foreach (var c in heading.Groups["text"].Value.ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(c) || c is '-' or '_')
                {
                    slug.Append(c);
                }
                else if (c == ' ')
                {
                    slug.Append('-');
                }
            }

            var candidate = slug.ToString();
            for (var n = 1; !slugs.Add(candidate); n++)
            {
                candidate = $"{slug}-{n}";
            }
        }

        return slugs;
    }

    private static string Resolve(string fromFile, string target)
    {
        var directory = Path.GetDirectoryName(Path.Combine(RepoRoot, fromFile))!;
        return ToRepoPath(Path.GetFullPath(Path.Combine(directory, Uri.UnescapeDataString(target))));
    }

    private static string ToRepoPath(string fullPath) =>
        Path.GetRelativePath(RepoRoot, fullPath).Replace('\\', '/').TrimEnd('/');

    private static string FindRepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Papuma.Kernel.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Repository root (Papuma.Kernel.slnx) not found above the test output.");
    }
}
