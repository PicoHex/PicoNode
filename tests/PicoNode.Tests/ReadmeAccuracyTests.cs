using System.Text.RegularExpressions;

namespace PicoNode.Tests;

/// <summary>
/// The READMEs measured against the thing they describe. Each rule here answers a question by
/// READING the build — the source tree, the csprojs, the sample projects — and comparing the
/// answer with what the doc claims. None of them bans a word: a banned phrase only catches the
/// particular sentences someone already regreted, while "18K lines" drifts into "20K" and a
/// five-row package table silently describes seven packages.
/// <para>
/// What replaced the old word list:
/// </para>
/// <list type="bullet">
///   <item><c>15K</c>/<c>17K</c> line bans → <see cref="No_readme_line_count_drift"/> counts the
///   files and lines under <c>src/</c> and holds the claim to ±5% of the measurement.</item>
///   <item>"PicoNode.Abs targets netstandard2.0" → the TFM in every module README, every layer
///   diagram annotation and every table cell must equal the csproj's, and
///   <see cref="Netstandard2_mentions_name_only_the_projects_that_are_on_it"/> refuses to let a
///   net10.0 project be described as netstandard.</item>
///   <item>"PicoNode.Http → PicoNode → PicoNode.Abs" → the arrows are parsed and every edge is
///   checked against the ProjectReference/PackageReference graph
///   (<see cref="Dependency_chains_stated_in_the_readmes_match_the_references"/>).</item>
///   <item>"Zero required runtime deps" → <see cref="Packable_projects_depend_only_on_picohex_packages"/>
///   measures the references of the seven packable projects.</item>
///   <item>The DTO bullet ban → <see cref="English_readme_states_which_generator_emits_what"/>
///   requires the four facts that make the sentence true (PWR001, PicoJetson.Gen,
///   <c>[PicoJsonSerializable]</c>, PCW003) instead of banning one bullet shape.</item>
/// </list>
/// </summary>
public sealed class ReadmeAccuracyTests
{
    /// <summary>
    /// How far the documented line count may drift from the measurement. A count is a snapshot, so
    /// exact equality would go red on the next <c>src/</c> commit; 5% is ~1,250 lines, enough for
    /// ordinary work and far too little for "18K" to pass against 25,125.
    /// </summary>
    private const double CountTolerance = 0.05;

    private static string RepoRoot =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    /// <summary>The translated READMEs at the repository root — discovered, not listed.</summary>
    private static string[] Readmes =>
        Directory
            .GetFiles(RepoRoot, "README*.md", SearchOption.TopDirectoryOnly)
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();

    // ── measurement ───────────────────────────────────────────────────────────────────────────

    /// <summary>Code size of <c>src/</c>, counted the way the README says it is counted.</summary>
    private static (int Files, int Lines) MeasureSource()
    {
        var files = SourceFiles().ToArray();

        return (files.Length, files.Sum(static path => File.ReadAllLines(path).Length));
    }

    private static IEnumerable<string> SourceFiles() =>
        Directory
            .EnumerateFiles(Path.Combine(RepoRoot, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(static path => !IsBuildOutput(path))
            .Order(StringComparer.Ordinal);

    private static bool IsBuildOutput(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    /// <summary>
    /// One project of <c>src/</c>, as its csproj declares it: the NuGet identity, the target
    /// framework, whether it ships, and the projects/packages it references. This record is the
    /// only source of truth these rules read; nothing below restates a csproj value.
    /// </summary>
    private sealed record Project(
        string Name,
        string CsprojPath,
        string PackageId,
        string Tfm,
        bool Packable,
        string[] Dependencies
    )
    {
        public bool DependsOn(string other) =>
            Dependencies.Contains(other, StringComparer.Ordinal);
    }

    private static Project[] MeasureProjects() =>
        Directory
            .EnumerateFiles(Path.Combine(RepoRoot, "src"), "*.csproj", SearchOption.AllDirectories)
            .Where(static path => !IsBuildOutput(path))
            .Order(StringComparer.Ordinal)
            .Select(Measure)
            .ToArray();

    /// <summary>
    /// The project name a <c>ProjectReference</c> points at, independent of the separator the csproj
    /// happens to use. The files store Windows separators even when checked out on Linux or macOS,
    /// where a backslash is an ordinary name character: <see cref="Path.GetFileNameWithoutExtension(string)"/>
    /// then returns the whole relative path, every dependency stops starting with <c>Pico</c>, and the
    /// dependency-model rule fails on every non-Windows runner while passing on the machine it was
    /// written on. Normalising first is what makes the rule platform-honest.
    /// </summary>
    private static string ProjectNameOf(string include) =>
        Path.GetFileNameWithoutExtension(include.Replace((char)92, '/'));

    private static Project Measure(string csprojPath)
    {
        var csproj = XDocument.Load(csprojPath);
        var name = Path.GetFileNameWithoutExtension(csprojPath);

        string? Value(string element) =>
            csproj.Descendants(element).Select(static x => x.Value.Trim()).FirstOrDefault();

        var packageId = Value("PackageId") ?? name;

        // A ProjectReference with ReferenceOutputAssembly="false" is a build-time (analyzer)
        // reference: it is not something a consumer of this package receives.
        var projectReferences = csproj
            .Descendants("ProjectReference")
            .Where(static reference =>
                !string.Equals(
                    reference.Attribute("ReferenceOutputAssembly")?.Value,
                    "false",
                    StringComparison.OrdinalIgnoreCase
                )
            )
            // The csproj stores Windows separators even when the repo is checked out on Linux or
            // macOS, where '\' is an ordinary name character: Path.GetFileNameWithoutExtension then
            // returns the whole relative path instead of the project name, every dependency stops
            // starting with "Pico", and this rule fails on every non-Windows runner while passing on
            // the machine it was written on. Normalise first; '/' is a separator on all three.
            .Select(static reference => ProjectNameOf(reference.Attribute("Include")!.Value));

        var packageReferences = csproj
            .Descendants("PackageReference")
            .Select(static reference => reference.Attribute("Include")?.Value)
            .Where(static value => !string.IsNullOrEmpty(value))!
            .Select(static value => value!);

        var dependencies = projectReferences
            .Concat(packageReferences)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();

        return new Project(
            name,
            csprojPath,
            packageId,
            Value("TargetFramework") ?? "<none>",
            string.Equals(Value("IsPackable"), "true", StringComparison.OrdinalIgnoreCase),
            dependencies
        );
    }

    /// <summary>
    /// Every number in a line, with the thousands separators this repository's translations use
    /// (comma, period, plain space, and the two Unicode spaces a French or Russian layout may
    /// carry) removed. "25,125", "25.125" and "25 125" all become 25125; a decimal like 1.5 would
    /// read as 15, which is why the claims compared below are counts, never decimals.
    /// </summary>
    private static int[] NumbersIn(string line) =>
        Regex
            .Matches(line, @"\d[\d.,\u00A0\u2009\u202F ]*\d|\d")
            .Select(static match => match.Value)
            .Select(static value =>
                int.TryParse(
                    Regex.Replace(value, @"[.,\u00A0\u2009\u202F ]", ""),
                    out var number
                )
                    ? number
                    : -1
            )
            .Where(static number => number >= 0)
            .ToArray();

    /// <summary>
    /// Runs of consecutive table rows (lines starting with <c>|</c>) — one entry per markdown table
    /// of a file.
    /// </summary>
    private static List<string[]> Tables(string[] lines)
    {
        var tables = new List<string[]>();
        var current = new List<string>();

        foreach (var line in lines)
        {
            if (line.TrimStart().StartsWith('|'))
            {
                current.Add(line);
                continue;
            }

            if (current.Count > 0)
            {
                tables.Add([.. current]);
                current.Clear();
            }
        }

        if (current.Count > 0)
            tables.Add([.. current]);

        return tables;
    }

    private static string[] Lines(string path) =>
        File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

    // ── the line-count claim, measured ────────────────────────────────────────────────────────

    [Test]
    public async Task No_readme_line_count_drift()
    {
        // The README states the size of `src/` and how it was counted ("bin//obj/ excluded"). That
        // sentence is reproducible, so the guard reproduces it: the claim must survive the
        // measurement within CountTolerance, in every language, whatever thousands separator the
        // translator used. This is what replaced the `15K`/`17K` bans - and it is what the `~18K`
        // claim would have failed the day it was written.
        var (files, lines) = MeasureSource();
        var findings = new List<string>();

        foreach (var path in Readmes)
        {
            var name = Path.GetFileName(path);
            var row = Lines(path)
                .Where(static line =>
                    line.Contains("src/", StringComparison.Ordinal)
                    && line.Contains("bin/", StringComparison.Ordinal)
                    && line.Contains("obj/", StringComparison.Ordinal)
                )
                .ToArray();

            if (row.Length != 1)
            {
                findings.Add(
                    $"{name}: expected exactly one line-count row mentioning src/ and bin//obj/, found {row.Length}"
                );
                continue;
            }

            var numbers = NumbersIn(row[0]);

            bool Claims(int actual) =>
                numbers.Any(claimed => Math.Abs(claimed - actual) <= Math.Max(1, actual * CountTolerance));

            if (!Claims(lines))
            {
                findings.Add(
                    $"{name}: no number in '{row[0].Trim()}' is within {CountTolerance:P0} of the measured "
                        + $"{lines:N0} lines of C# under src/"
                );
            }

            if (!Claims(files))
            {
                findings.Add(
                    $"{name}: no number in '{row[0].Trim()}' is within {CountTolerance:P0} of the measured "
                        + $"{files:N0} C# files under src/"
                );
            }
        }

        await Assert
            .That(string.Join(" | ", findings))
            .IsEqualTo("")
            .Because("the line count is reproducible, so the doc must reproduce it");

        await Assert
            .That(Readmes.Length)
            .IsGreaterThanOrEqualTo(10)
            .Because("the ten translated READMEs must all be measured, not the English one alone");

        await Assert
            .That(lines)
            .IsGreaterThan(1000)
            .Because($"the measurement must see a real tree, not an empty walk ({files} files found)");
    }

    // ── mandatory module metadata, checked against the csproj ────────────────────────────────

    [Test]
    public async Task Module_readmes_declare_tfm_and_dependencies()
    {
        // Both fields are REQUIRED. They used to be optional: the rule only compared them when a
        // README happened to carry them, so the two generator READMEs declared a TFM, no
        // dependencies, and nothing noticed. A missing field is now a failure, and a present field
        // is compared with the csproj (a moved TFM or a pruned reference cannot go unnoticed).
        var findings = new List<string>();

        foreach (var project in MeasureProjects())
        {
            var readmePath = Path.Combine(Path.GetDirectoryName(project.CsprojPath)!, "README.md");

            if (!File.Exists(readmePath))
            {
                findings.Add($"{project.Name}: no README.md beside the csproj");
                continue;
            }

            var text = File.ReadAllText(readmePath);

            var tfm = Regex.Match(text, @"\*\*TFM\*\*: `([^`]+)`");
            if (!tfm.Success)
            {
                findings.Add($"{project.Name}: README.md declares no '**TFM**: `…`' line");
            }
            else if (tfm.Groups[1].Value != project.Tfm)
            {
                findings.Add(
                    $"{project.Name}: README TFM '{tfm.Groups[1].Value}' != csproj '{project.Tfm}'"
                );
            }

            var dependencies = Regex.Match(text, @"\*\*Dependencies\*\*:([^\r\n]*)");
            if (!dependencies.Success)
            {
                findings.Add(
                    $"{project.Name}: README.md declares no '**Dependencies**: …' line "
                        + $"(the csproj references [{string.Join(", ", project.Dependencies)}])"
                );
                continue;
            }

            var documented = Regex
                .Matches(dependencies.Groups[1].Value, "`([^`]+)`")
                .Select(static match => match.Groups[1].Value)
                .Where(static value => value.StartsWith("Pico", StringComparison.Ordinal))
                .OrderBy(static value => value, StringComparer.Ordinal)
                .ToArray();

            var actual = project
                .Dependencies.Where(static value => value.StartsWith("Pico", StringComparison.Ordinal))
                .OrderBy(static value => value, StringComparer.Ordinal)
                .ToArray();

            if (!documented.SequenceEqual(actual, StringComparer.Ordinal))
            {
                findings.Add(
                    $"{project.Name}: README deps [{string.Join(", ", documented)}] != csproj "
                        + $"[{string.Join(", ", actual)}]"
                );
            }
        }

        await Assert
            .That(MeasureProjects().Length)
            .IsGreaterThanOrEqualTo(9)
            .Because("the walk must see every project under src/");

        await Assert
            .That(string.Join(" | ", findings))
            .IsEqualTo("")
            .Because("module README metadata is mandatory and must match the csproj");
    }

    // ── dependency claims, measured against the reference graph ──────────────────────────────

    [Test]
    public async Task Dependency_chains_stated_in_the_readmes_match_the_references()
    {
        // The old rule banned one literal arrow chain. This one parses every `A → B → C` line and
        // asks the csprojs whether each edge exists, so it rejects any invented edge (the one that
        // used to be banned included) and it also notices an edge that was REMOVED from the code
        // while the doc still advertises it.
        var projects = MeasureProjects();
        var names = projects.Select(static project => project.Name).ToHashSet(StringComparer.Ordinal);
        var findings = new List<string>();
        var chains = 0;

        foreach (var path in Readmes)
        {
            var name = Path.GetFileName(path);
            var lines = Lines(path);

            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index];
                if (!line.Contains('→'))
                    continue;

                var cells = line.Split('→').Select(static cell => cell.Trim()).ToArray();

                // A line is a dependency chain only when every cell is a project or package name;
                // the translations also use `→` in prose (`WebContext` → the current context).
                if (cells.Length < 2 || !cells.All(names.Contains))
                    continue;

                chains++;

                for (var i = 0; i < cells.Length - 1; i++)
                {
                    var from = projects.First(project => project.Name == cells[i]);
                    var to = projects.First(project => project.Name == cells[i + 1]);

                    if (!from.DependsOn(to.Name) && !from.DependsOn(to.PackageId))
                    {
                        findings.Add(
                            $"{name}:{index + 1}: '{cells[i]} → {cells[i + 1]}' "
                                + $"is not a reference edge ({from.Name} references "
                                + $"[{string.Join(", ", from.Dependencies)}])"
                        );
                    }
                }
            }
        }

        await Assert
            .That(chains)
            .IsGreaterThanOrEqualTo(Readmes.Length * 2)
            .Because("each README draws the two dependency chains this rule reads");

        await Assert
            .That(string.Join(" | ", findings))
            .IsEqualTo("")
            .Because("an arrow in the docs must be a reference in the csprojs");
    }

    [Test]
    public async Task Tfm_annotations_in_the_layer_diagram_match_the_csprojs()
    {
        // The layer diagram annotates each layer with its target framework. Those annotations are
        // how the old "PicoNode.Abs targets netstandard2.0" lie was told, so they are compared with
        // the csprojs rather than trusted.
        var projects = MeasureProjects();
        var byName = projects.ToDictionary(static project => project.Name, StringComparer.Ordinal);
        var findings = new List<string>();
        var annotations = 0;

        foreach (var path in Readmes)
        {
            var name = Path.GetFileName(path);
            var found = 0;

            foreach (var line in Lines(path))
            {
                var firstToken = line.TrimStart().Split(' ', '\t')[0];

                if (!byName.TryGetValue(firstToken, out var project))
                    continue;

                var annotation = Regex.Match(line, @"\((net(?:standard)?\d+\.\d+)[,)]");
                if (!annotation.Success)
                    continue;

                found++;
                annotations++;

                if (annotation.Groups[1].Value != project.Tfm)
                {
                    findings.Add(
                        $"{name}: '{firstToken}' is annotated {annotation.Groups[1].Value}, csproj says {project.Tfm}"
                    );
                }
            }

            await Assert
                .That(found)
                .IsGreaterThanOrEqualTo(6)
                .Because($"{name} must keep annotating its layers with their TFM");
        }

        await Assert
            .That(annotations)
            .IsGreaterThanOrEqualTo(60)
            .Because("ten READMEs × the six net10.0 layers must all be read");

        await Assert
            .That(string.Join(" | ", findings))
            .IsEqualTo("")
            .Because("a layer's TFM in the diagram must be the csproj's");
    }

    [Test]
    public async Task Package_tables_list_exactly_the_packable_projects()
    {
        // Two tables list the shipped packages and one of them also states each package's TFM. The
        // tables said five packages while seven shipped, and nothing rhymed the row count with
        // `IsPackable`; now the row SET is the measurement, in both directions.
        var projects = MeasureProjects();
        var packable = projects
            .Where(static project => project.Packable)
            .Select(static project => project.PackageId)
            .OrderBy(static id => id, StringComparer.Ordinal)
            .ToArray();
        var known = projects
            .SelectMany(static project => new[] { project.Name, project.PackageId })
            .ToHashSet(StringComparer.Ordinal);
        var byPackage = projects.ToDictionary(static project => project.PackageId, StringComparer.Ordinal);

        var findings = new List<string>();

        foreach (var path in Readmes)
        {
            var name = Path.GetFileName(path);

            // Group the table rows (consecutive `|` lines) into tables: a table that talks about
            // packages must list ALL of them, because a reader choosing a package reads one table.
            foreach (var table in Tables(Lines(path)))
            {
                var listed = new HashSet<string>(StringComparer.Ordinal);

                foreach (var line in table)
                {
                    var cells = line.Split('|').Select(static cell => cell.Trim()).ToArray();
                    if (cells.Length < 4)
                        continue;

                    var bold = Regex.Match(cells[1], @"^\*\*([^*]+)\*\*$");
                    if (!bold.Success)
                        continue;

                    var package = bold.Groups[1].Value.Trim();
                    if (!known.Contains(package))
                        continue; // a feature row (`**Line count**`), not a package row

                    listed.Add(package);

                    // A cell that is exactly a TFM is a TFM claim; compare it with the csproj.
                    var tfmCell = cells[2];
                    if (byPackage.TryGetValue(package, out var project)
                        && Regex.IsMatch(tfmCell, @"^(net|netstandard)\d+\.\d+$")
                        && tfmCell != project.Tfm)
                    {
                        findings.Add($"{name}: '{package}' is listed as {tfmCell}, csproj says {project.Tfm}");
                    }
                }

                if (listed.Count == 0)
                    continue; // a table about features, not about packages

                var missing = packable.Where(package => !listed.Contains(package)).ToArray();
                var extra = listed.Where(package => !packable.Contains(package, StringComparer.Ordinal)).ToArray();

                if (missing.Length > 0)
                {
                    findings.Add(
                        $"{name}: the table starting '{table[0].Trim()}' omits [{string.Join(", ", missing)}]"
                    );
                }

                if (extra.Length > 0)
                {
                    findings.Add(
                        $"{name}: the table starting '{table[0].Trim()}' lists "
                            + $"[{string.Join(", ", extra)}], which nothing ships"
                    );
                }
            }
        }

        await Assert
            .That(packable.Length)
            .IsGreaterThanOrEqualTo(7)
            .Because("the measurement must find the packable projects");

        await Assert
            .That(string.Join(" | ", findings))
            .IsEqualTo("")
            .Because("the package tables must describe exactly the packages that ship");
    }

    [Test]
    public async Task Netstandard2_mentions_name_only_the_projects_that_are_on_it()
    {
        // Replaces the "PicoNode.Abs … netstandard2.0" ban: instead of forbidding one sentence, the
        // rule reads every line that mentions netstandard2.0 in the docs that live next to the code
        // and requires each project it names to actually be on netstandard2.0.
        var projects = MeasureProjects();
        var netstandard = projects.Where(static project => project.Tfm != "net10.0").ToArray();
        var rootReadmes = Readmes;
        var moduleReadmes = projects
            .Select(static project => Path.Combine(Path.GetDirectoryName(project.CsprojPath)!, "README.md"))
            .Where(File.Exists)
            .ToArray();

        var findings = new List<string>();
        var mentions = 0;

        foreach (var path in rootReadmes.Concat(moduleReadmes))
        {
            var name = Path.GetRelativePath(RepoRoot, path);

            foreach (var line in Lines(path))
            {
                if (
                    !line.Contains("netstandard2.0", StringComparison.Ordinal)
                    && !line.Contains("Standard 2.0", StringComparison.Ordinal)
                )
                {
                    continue;
                }

                mentions++;

                var wrong = projects
                    .Where(project => project.Tfm == "net10.0")
                    .Where(project => Regex.IsMatch(line, $@"(?<![\w.]){Regex.Escape(project.Name)}(?![\w.])"))
                    .Select(static project => project.Name)
                    .ToArray();

                if (wrong.Length > 0)
                {
                    findings.Add(
                        $"{name}: '{line.Trim()}' describes [{string.Join(", ", wrong)}] as netstandard2.0; "
                            + $"only [{string.Join(", ", netstandard.Select(static p => p.Name))}] are"
                    );
                }
            }
        }

        await Assert
            .That(mentions)
            .IsGreaterThan(0)
            .Because("the generator READMEs do state their netstandard2.0 TFM, so the walk must see it");

        await Assert
            .That(string.Join(" | ", findings))
            .IsEqualTo("")
            .Because("netstandard2.0 is a fact about a csproj, not a claim to be trusted");
    }

    /// <summary>
    /// Pins the platform trap directly, because the rule above can only fail on a runner this
    /// repository is not developed on: a Windows-style reference path must yield the project name on
    /// every OS, or the dependency-model rule silently becomes "true on the author's machine only".
    /// </summary>
    [Test]
    public async Task Project_reference_paths_are_resolved_the_same_way_on_every_platform()
    {
        // Written with explicit char codes so the test itself stays copy-pasteable between platforms.
        var windowsStyle = $"..{(char)92}..{(char)92}Network{(char)92}PicoNode.Abs{(char)92}PicoNode.Abs.csproj";
        var unixStyle = "../../Network/PicoNode.Abs/PicoNode.Abs.csproj";
        var bare = "PicoNode.Abs.csproj";

        await Assert.That(ProjectNameOf(windowsStyle)).IsEqualTo("PicoNode.Abs");
        await Assert.That(ProjectNameOf(unixStyle)).IsEqualTo("PicoNode.Abs");
        await Assert.That(ProjectNameOf(bare)).IsEqualTo("PicoNode.Abs");
    }

    [Test]
    public async Task Packable_projects_depend_only_on_picohex_packages()
    {
        // The "Why PicoNode?" table claims "No Microsoft framework deps (PicoHex-native libraries
        // only)". That is measurable: every reference of every shipped package. This replaced the
        // "Zero required runtime deps" ban, which forbade a phrase instead of checking the claim.
        var projects = MeasureProjects().Where(static project => project.Packable).ToArray();

        var offending = projects
            .SelectMany(project =>
                project
                    .Dependencies.Where(static dependency =>
                        !dependency.StartsWith("Pico", StringComparison.Ordinal)
                        && !dependency.StartsWith("Microsoft.NET.Test", StringComparison.Ordinal)
                    )
                    .Select(dependency => $"{project.PackageId} → {dependency}")
            )
            .ToArray();

        // The row exists in all ten READMEs; the two proper nouns survive translation, which is
        // all this check needs (it is about the row being there, not about its wording).
        var withoutClaim = Readmes
            .Where(path =>
                !Lines(path).Any(static line =>
                    line.Contains("PicoHex", StringComparison.Ordinal)
                    && line.Contains("Microsoft", StringComparison.Ordinal)
                )
            )
            .Select(Path.GetFileName)
            .ToArray();

        await Assert
            .That(withoutClaim)
            .IsEmpty()
            .Because("every README keeps the dependency-model row this rule makes true");

        await Assert
            .That(offending)
            .IsEmpty()
            .Because(
                "the dependency-model row is only true while every shipped package references PicoHex libraries only; offenders: "
                    + string.Join(", ", offending)
            );
    }

    [Test]
    public async Task Test_counts_table_matches_the_test_sources()
    {
        // tests/README.md publishes a per-project count and a total. Both are the kind of number
        // this task exists for: the total is DERIVED here from the rows (so it cannot disagree with
        // them) and each row is measured from that project's sources. Measured at the time this
        // rule was added, all ten rows were stale and the total was 56 short.
        // Every directory under tests/ owns a row - including the two suites that have no
        // `[Test]` count (a Python harness and an executable verifier), because a new directory
        // appearing WITHOUT a row is exactly the drift this rule is for.
        var projects = Directory
            .EnumerateDirectories(Path.Combine(RepoRoot, "tests"))
            .OrderBy(static directory => Path.GetFileName(directory), StringComparer.Ordinal)
            .ToArray();

        var measured = projects.ToDictionary(
            static directory => Path.GetFileName(directory),
            static directory =>
                Directory
                    .EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
                    .Where(static path => !IsBuildOutput(path))
                    .Sum(path =>
                        Regex.Matches(File.ReadAllText(path), @"(?m)^[ 	]*\[Test\][ 	 \r]*$").Count
                    ),
            StringComparer.Ordinal
        );

        var table = Lines(Path.Combine(RepoRoot, "tests", "README.md"));
        var findings = new List<string>();
        var rows = new List<(string Project, int? Count, string CountCell)>();

        foreach (var line in table.Where(static line => line.TrimStart().StartsWith("| `", StringComparison.Ordinal)))
        {
            var cells = line.Split('|').Select(static cell => cell.Trim()).ToArray();
            if (cells.Length < 5)
                continue;

            var project = cells[1].Trim('`');
            rows.Add((project, int.TryParse(cells[3], out var count) ? count : null, cells[3]));
        }

        var documented = rows.Select(static row => row.Project).ToHashSet(StringComparer.Ordinal);
        var missing = measured.Keys.Where(key => !documented.Contains(key)).ToArray();
        var extra = documented.Where(key => !measured.ContainsKey(key)).ToArray();

        if (missing.Length > 0)
            findings.Add($"tests/README.md: no row for [{string.Join(", ", missing)}]");
        if (extra.Length > 0)
            findings.Add($"tests/README.md: rows for [{string.Join(", ", extra)}], which have no csproj");

        foreach (var (project, count, countCell) in rows)
        {
            if (countCell.Length == 0)
            {
                findings.Add($"tests/README.md: '{project}' has neither a count nor a reason");
                continue;
            }

            if (measured.TryGetValue(project, out var actual) && count is { } claimed && claimed != actual)
            {
                findings.Add($"tests/README.md: '{project}' claims {claimed} tests, sources declare {actual} [Test]");
            }
        }

        // The total is derived from the rows, so a row edit that forgets the total fails here.
        var total = rows.Sum(static row => row.Count ?? 0);
        var totalRow = table.FirstOrDefault(static line =>
            line.Contains("project table above", StringComparison.Ordinal)
        );
        int[] claimedTotals = totalRow is null ? [] : NumbersIn(totalRow);

        if (totalRow is null)
            findings.Add("tests/README.md: the total claim ('… across the project table above') is gone");

        await Assert
            .That(measured.Count)
            .IsGreaterThanOrEqualTo(10)
            .Because("the walk must see every test project");

        await Assert
            .That(string.Join(" | ", findings))
            .IsEqualTo("")
            .Because("a published test count is a measurement, not a memory");

        await Assert
            .That(claimedTotals.Any(claimed => claimed == total))
            .IsTrue()
            .Because(
                $"the stated total must be the sum of the rows ({total:N0}); the line says "
                    + $"'{totalRow?.Trim()}'"
            );
    }

    // ── required content that no count can express ───────────────────────────────────────────

    [Test]
    public async Task English_readme_states_which_generator_emits_what()
    {
        // The four facts that make the "generators" section true. This replaced a rule that banned
        // one bullet SHAPE ("a bullet naming both a generator and [PicoJsonSerializable]"), which
        // caught the wrong sentence and left the right one free to disappear.
        var text = File.ReadAllText(Path.Combine(RepoRoot, "README.md"));

        await Assert.That(text).Contains("build-time diagnostic (PWR001)");
        await Assert.That(text).Contains("PicoJetson.Gen");
        await Assert.That(text).Contains("[PicoJsonSerializable]");
        await Assert
            .That(text)
            .Contains("PCW003")
            .Because("the unmarked-DTO failure mode is only useful if its diagnostic is named");
    }

    [Test]
    public async Task Every_readme_documents_the_async_multipart_api_and_its_limits()
    {
        // The old examples called a synchronous Parse(...) that does not exist, so the rule is
        // stated as two measurements: the API the README calls must be the only one the type has,
        // and the limits it mentions must be the options that enforce them.
        var parseMethods = typeof(PicoNode.Web.MultipartFormDataParser)
            .GetMethods()
            .Where(static method => method.Name == "Parse")
            .ToArray();

        await Assert
            .That(parseMethods.Length)
            .IsEqualTo(0)
            .Because("the rule below forbids a synchronous Parse in the docs BECAUSE it does not exist");

        var offenders = Readmes
            .SelectMany(path =>
            {
                var text = File.ReadAllText(path);
                var problems = new List<string>();
                if (!text.Contains("MultipartFormDataParser.ParseAsync", StringComparison.Ordinal))
                    problems.Add("missing MultipartFormDataParser.ParseAsync");

                if (text.Contains("MultipartFormDataParser.Parse(", StringComparison.Ordinal))
                    problems.Add("uses the nonexistent synchronous Parse(...) API");

                foreach (
                    var option in new[] { "MaxPartSizeBytes", "MaxTotalSizeBytes", "MaxBoundaryLength" }
                )
                {
                    var declared = typeof(PicoNode.Web.MultipartFormDataParserOptions)
                        .GetProperty(option)
                        ?.Name;

                    if (declared is null)
                        problems.Add($"the docs name '{option}', which the options type does not have");
                    else if (!text.Contains(option, StringComparison.Ordinal))
                        problems.Add($"missing {option}");
                }

                if (!text.Contains("({file.Content.Length} bytes)", StringComparison.Ordinal))
                    problems.Add("broken multipart file-size example");

                return problems.Select(problem => $"{Path.GetFileName(path)}: {problem}");
            })
            .ToArray();

        await Assert.That(offenders.Length).IsEqualTo(0);
    }
}
