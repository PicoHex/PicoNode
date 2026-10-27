using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;

namespace PicoJsonRpc.Tests;

/// <summary>
/// The guard for the ONE number that has drifted four times: how many tests a
/// node-less run loses. Prose is what kept drifting, so the number now has a single
/// home (<see cref="SkipUnlessNodeAttribute.NodeDependentTestCount"/>) and this class
/// fails when that home and reality disagree:
/// <list type="number">
/// <item>the constant equals the number of test methods actually decorated with
/// <c>[SkipUnlessNode]</c> — read from the ATTRIBUTE METADATA of the built assembly,
/// because a source grep cannot tell a decorated method from a comment that names
/// the attribute;</item>
/// <item>the rendered skip reason quotes that constant and quotes the per-class
/// breakdown the assembly really has (<c>LifecycleTests 8/9</c>, …), so the loud
/// sentence cannot carry a stale number of its own;</item>
/// <item>no workflow or document restates the count next to the phrase it belongs to
/// (<c>&lt;number&gt; child-process tests</c>, <c>&lt;number&gt; node-dependent
/// tests</c>) with a value other than the constant — the exact shape every past drift
/// took (<c>release.yml</c> "20 child-process", <c>CiWorkflowTests</c> "20"/"fifth",
/// <c>NodeFixture.cs</c> "19 probes… 19 child").</item>
/// </list>
/// <para>
/// Deliberately NOT asserted: "no file contains the digits 21". A number in another
/// context (a port, a version, a timeout) is not a loss count, and a guard that fails
/// on those would be switched off within a week.
/// </para>
/// </summary>
public sealed class NodeLessLossCountTests
{
    /// <summary>The single source of truth, as a number.</summary>
    private static int DeclaredCount =>
        int.Parse(
            SkipUnlessNodeAttribute.NodeDependentTestCount,
            CultureInfo.InvariantCulture
        );

    /// <summary>
    /// The guard: the constant, the decoration it describes, and the prose that
    /// quotes it — each of which has been wrong at least once.
    /// </summary>
    [Test]
    public async Task The_skip_count_is_derived_from_the_decorated_tests()
    {
        var decorated = DecoratedTestMethods();

        await Assert
            .That(decorated.Count)
            .IsGreaterThan(0)
            .Because("non-vacuity: the suite must decorate test methods with [SkipUnlessNode]");

        // (1) the constant IS the decoration count - not a number kept in step by hand.
        await Assert
            .That(DeclaredCount)
            .IsEqualTo(decorated.Count)
            .Because(
                "SkipUnlessNodeAttribute.NodeDependentTestCount is the single source of truth for "
                    + "how many tests a node-less run loses, so a new [SkipUnlessNode] test must bump "
                    + "it. Decorated today: "
                    + string.Join(", ", decorated)
            );

        // (2) the loud sentence is built FROM the constant, not beside it.
        await Assert
            .That(SkipUnlessNodeAttribute.NodeLessRunReason)
            .Contains($"{SkipUnlessNodeAttribute.NodeDependentTestCount} node-dependent tests")
            .Because("the skip reason must interpolate the constant, not restate a number");

        // (3) ...and its per-class breakdown (8/9, 12/13, 1/7) is the real one: those
        // fractions have their own drift history (T7.6c corrected exactly this).
        var breakdown = Breakdown();
        var decoratedInBreakdown = breakdown.Values.Sum(static v => v.Decorated);

        await Assert
            .That(decoratedInBreakdown)
            .IsEqualTo(DeclaredCount)
            .Because(
                "the per-class breakdown the skip reason quotes must add up to the constant: "
                    + string.Join(", ", breakdown.Select(static p => $"{p.Key} {p.Value.Decorated}/{p.Value.Total}"))
            );

        foreach (var (className, counts) in breakdown)
        {
            if (counts.Decorated == 0)
            {
                continue; // a class with nothing skipped is not in the breakdown
            }

            await Assert
                .That(SkipUnlessNodeAttribute.NodeLessRunReason)
                .Contains($"{className} {counts.Decorated}/{counts.Total}")
                .Because(
                    $"the skip reason must quote '{className} {counts.Decorated}/{counts.Total}', "
                        + "which is what the assembly actually contains"
                );
        }
    }

    /// <summary>
    /// The prose guard: every "<c>N child-process tests</c>" / "<c>N node-dependent
    /// tests</c>" in a workflow or document must use the constant. This is the shape
    /// the drift took, so it is the shape that is checked.
    /// </summary>
    [Test]
    public async Task No_document_quotes_a_stale_skip_count()
    {
        var stale = new List<string>();
        var seen = 0;

        foreach (var path in ProseFiles())
        {
            var text = await File.ReadAllTextAsync(path);

            foreach (Match match in CountMention.Matches(text))
            {
                seen++;
                var quoted = match.Groups["n"].Value;

                if (quoted != SkipUnlessNodeAttribute.NodeDependentTestCount)
                {
                    stale.Add(
                        $"{Path.GetRelativePath(RepositoryRoot(), path)}: '{match.Value}' "
                            + $"(the count is {SkipUnlessNodeAttribute.NodeDependentTestCount})"
                    );
                }
            }
        }

        // Non-vacuity: if the regex stopped matching the phrase (or the file list
        // emptied) this test would pass on zero evidence.
        await Assert
            .That(seen)
            .IsGreaterThan(0)
            .Because("at least one workflow restates the count, so the matcher must see it");

        await Assert
            .That(stale)
            .IsEmpty()
            .Because(
                "the skip count has one home (SkipUnlessNodeAttribute.NodeDependentTestCount); "
                    + "prose that restates it must agree with it"
            );
    }

    /// <summary>
    /// A count quoted for the suite's loss: a number immediately followed by the
    /// phrase it counts. Kept narrow on purpose (see the class doc).
    /// </summary>
    private static readonly Regex CountMention = new(
        @"(?<n>\d+)\s+(?:child-process|child process|node-dependent|node dependent)\s+tests?\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    /// <summary>
    /// The repository root, found by walking up to the directory holding
    /// <c>PicoNode.slnx</c>. It throws rather than returning null: a guard that
    /// silently walked an empty list would pass while proving nothing.
    /// </summary>
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PicoNode.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"PicoNode.slnx not found above '{AppContext.BaseDirectory}', so the prose guard has nothing to check"
        );
    }

    /// <summary>
    /// Every file that has carried the count or could: the workflows, the
    /// repository's agent instructions, the top-level documents and this suite's own
    /// sources. Scoped rather than a full-tree walk so the guard stays fast and does
    /// not police build output.
    /// </summary>
    private static IEnumerable<string> ProseFiles()
    {
        var root = RepositoryRoot();
        var agents = Path.Combine(root, "AGENTS.md");

        foreach (
            var path in Directory.EnumerateFiles(Path.Combine(root, ".github", "workflows"), "*.yml")
        )
        {
            yield return path;
        }

        if (File.Exists(agents))
        {
            yield return agents;
        }

        foreach (var path in Directory.EnumerateFiles(root, "*.md"))
        {
            if (!string.Equals(path, agents, StringComparison.OrdinalIgnoreCase))
            {
                yield return path;
            }
        }

        foreach (
            var path in Directory.EnumerateFiles(
                Path.Combine(root, "tests"),
                "*.cs",
                SearchOption.AllDirectories
            )
        )
        {
            if (
                path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            )
            {
                continue;
            }

            yield return path;
        }
    }

    /// <summary>
    /// The test methods of this assembly carrying <see cref="SkipUnlessNodeAttribute"/>,
    /// as <c>Class.Method</c>. Reflection over ATTRIBUTE METADATA is the source of
    /// truth here: the skip is decided per method at registration, so the count is a
    /// property of the built assembly.
    /// </summary>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "Test assembly: never trimmed or AOT-published, and reading the [SkipUnlessNode] metadata IS the assertion."
    )]
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2070",
        Justification = "Test assembly: never trimmed or AOT-published, and reading the [SkipUnlessNode] metadata IS the assertion."
    )]
    private static IReadOnlyList<string> DecoratedTestMethods() =>
        [
            .. typeof(NodeFixtureTests)
                .Assembly.GetTypes()
                .SelectMany(TestMethodMethods)
                .Where(static method => method.GetCustomAttributesData().Any(IsSkipUnlessNode))
                .Select(static method => $"{method.DeclaringType!.Name}.{method.Name}")
                .Order(StringComparer.Ordinal),
        ];

    /// <summary>
    /// Per test class: how many of its methods are decorated, and how many it has in
    /// total — the <c>8/9</c> shape the skip reason quotes.
    /// </summary>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "Test assembly: never trimmed or AOT-published, and reading the attribute metadata IS the assertion."
    )]
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2070",
        Justification = "Test assembly: never trimmed or AOT-published, and reading the attribute metadata IS the assertion."
    )]
    private static Dictionary<string, (int Decorated, int Total)> Breakdown()
    {
        var breakdown = new Dictionary<string, (int Decorated, int Total)>(StringComparer.Ordinal);

        foreach (var type in typeof(NodeFixtureTests).Assembly.GetTypes())
        {
            if (type.Namespace != typeof(NodeFixtureTests).Namespace)
            {
                continue;
            }

            var tests = TestMethodMethods(type)
                .Where(static method =>
                    method.GetCustomAttributesData().Any(static a => a.AttributeType.Name == "TestAttribute")
                )
                .ToArray();

            if (tests.Length == 0)
            {
                continue;
            }

            breakdown[type.Name] = (
                tests.Count(static method => method.GetCustomAttributesData().Any(IsSkipUnlessNode)),
                tests.Length
            );
        }

        return breakdown;
    }

    private static IEnumerable<MethodInfo> TestMethodMethods(Type type) =>
        type.GetMethods(
            BindingFlags.Public
                | BindingFlags.NonPublic
                | BindingFlags.Instance
                | BindingFlags.Static
                | BindingFlags.DeclaredOnly
        );

    private static bool IsSkipUnlessNode(CustomAttributeData attribute) =>
        attribute.AttributeType == typeof(SkipUnlessNodeAttribute);
}
