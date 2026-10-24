namespace PicoJsonRpc.Tests;

/// <summary>
/// The two holes F-98 found in <c>scripts/release.ps1</c>, asserted on what the script
/// PRINTS and on its EXIT CODE — by running it against <see cref="ReleaseScriptProbe"/>,
/// never by grepping its text.
/// <para>
/// <b>Hole 1 — a bootstrap was reported as drift.</b> The baseline-drift guard
/// (<c>release.ps1</c>, "a baseline holds the LAST RELEASE's surface") compares every
/// committed baseline with the file at the last tag. When the file did not exist at that
/// tag the guard still counted it as "refreshed before this run", so the FIRST release
/// after <c>-Bootstrap</c> — the one release where every baseline is by definition absent
/// at the tag — was always refused with "baselines are ahead of &lt;tag&gt;". A guard that
/// refuses the correct case teaches the operator to work around the guard, and the whole
/// point of this one is a y bump that hides an x bump. The absent-at-the-tag case is
/// bootstrap, not drift.
/// </para>
/// <para>
/// <b>Hole 2 — dropping a package was not an API change.</b> The API diff iterates the
/// projects that exist NOW, so a package that was deleted cannot appear in it: its
/// baseline simply stopped being compared, and the stale file was removed at tag time
/// with a note. The release then shipped as a <c>y</c> bump — the version rule's promise
/// that nothing a consumer can observe changed — while one of their packages ceased to
/// exist. Deleting a package is an <c>x</c> bump, so the release has to see it.
/// </para>
/// <para>
/// Four runs, all of them real process launches: a dry run for the bootstrap verdict and
/// the two dropped-package shapes, and a REAL run (no <c>-DryRun</c>) for the drift
/// refusal — because "the first release after <c>-Bootstrap</c> is always refused" is a
/// statement about a release <i>going through</i>, which only a real run can show. The
/// real runs are safe here: the sandbox is a throwaway repository on <c>main</c> with a
/// stubbed <c>dotnet</c>, no <c>-Push</c>, and every write lands under the temp directory.
/// </para>
/// </summary>
public sealed class ReleaseGuardHolesTests
{
    /// <summary>
    /// Hole 1, first half. A baseline committed at HEAD but absent at the last tag is a
    /// BOOTSTRAP: the dry run must say so instead of accusing the worktree of drift, and a
    /// real release must actually go through and tag.
    /// <para>
    /// On the pre-fix script the absent-at-the-tag file lands in the <c>$refreshed</c> list,
    /// so the dry run prints "baselines already match this worktree, but not v2026.5.1
    /// (Later (absent at v2026.5.1))" — drift language for a package that was never
    /// released — and the real run exits 2 with "baselines are ahead of v2026.5.1" without
    /// creating a tag. Reverting <c>-Bootstrap</c> to "run it once and then never release"
    /// is the defect this pins.
    /// </para>
    /// </summary>
    [Test]
    [SkipUnlessReleaseScriptHost]
    public async Task A_baseline_absent_at_the_last_tag_is_bootstrap_not_drift()
    {
        using var probe = ReleaseScriptProbe.Create();

        // The shape of the first release after `-Bootstrap`: a package whose baseline is
        // committed here and did not exist at the last tag.
        probe.AddPackableProject("Later", withBaseline: true);
        probe.Commit("probe: a package the last tag never released");

        var dry = probe.Run();
        await Assert.That(dry.ExitCode).IsEqualTo(0).Because(dry.Explain);
        await Assert
            .That(dry.StandardOutput)
            .Contains($"Later: no baseline at {ReleaseScriptProbe.LastTag} - BOOTSTRAP, not drift")
            .Because(
                "a package that did not exist at the last tag has no released surface to "
                    + "compare against; calling that drift is what refused the first release "
                    + "after -Bootstrap. "
                    + dry.Explain
            );
        await Assert
            .That(dry.StandardOutput)
            .DoesNotContain("baselines already match this worktree, but not")
            .Because(
                "the drift accusation is for baselines that were refreshed over an "
                    + "EXISTING one; printing it here is the false positive F-98 reported. "
                    + dry.Explain
            );

        // ...and the release really goes through: no refusal, and a tag at the end.
        var release = probe.RunRelease();
        await Assert.That(release.ExitCode).IsEqualTo(0).Because(release.Explain);
        await Assert
            .That(probe.TagNames())
            .Contains("v" + ReleaseScriptProbe.StubNextVersion)
            .Because(
                "the first release after -Bootstrap must create a tag; a guard that refuses "
                    + "it leaves only `git tag` by hand, which is the process release.ps1 "
                    + "exists to prevent. "
                    + release.Explain
            );
        await Assert
            .That(release.StandardOutput)
            .DoesNotContain("baselines are ahead of")
            .Because("the bootstrap case must not reach the drift refusal. " + release.Explain);
    }

    /// <summary>
    /// Hole 1, second half — the escape hatch must not become the whole guard. A baseline
    /// that WAS released and has been edited since is genuine drift: a real run must still
    /// refuse it (exit 2, no tag), and only an explicit <c>-ForceBaselineDrift</c> may
    /// acknowledge it, printing the delta it is overriding.
    /// <para>
    /// This half exists because the cheap "fix" for hole 1 is to delete the guard. On the
    /// pre-fix script the refusal passes and <c>-ForceBaselineDrift</c> does not exist yet,
    /// so the acknowledged run fails parameter binding (exit 1) — the second assertion below
    /// is the one that is red first.
    /// </para>
    /// </summary>
    [Test]
    [SkipUnlessReleaseScriptHost]
    public async Task A_released_baseline_that_was_edited_is_still_refused_unless_acknowledged()
    {
        using var probe = ReleaseScriptProbe.Create();
        probe.WriteBaseline("Fake", "M: Fake.Types.Fake\nM: Fake.Types.HandEditedAfterTheTag\n");
        probe.Commit("probe: a baseline refreshed before the release");

        var refused = probe.RunRelease();
        await Assert.That(refused.ExitCode).IsEqualTo(2).Because(refused.Explain);
        await Assert
            .That(refused.StandardOutput)
            .Contains($"baselines are ahead of {ReleaseScriptProbe.LastTag}")
            .Because(
                "a baseline that exists at the last tag and no longer matches it hides an "
                    + "API delta; the y bump it produces is exactly what this guard is for. "
                    + refused.Explain
            );
        await Assert
            .That(probe.TagNames())
            .DoesNotContain("v" + ReleaseScriptProbe.StubNextVersion)
            .Because("a refused release must not tag anything. " + refused.Explain);

        var acknowledged = probe.RunRelease("-ForceBaselineDrift");
        await Assert.That(acknowledged.ExitCode).IsEqualTo(0).Because(acknowledged.Explain);
        await Assert
            .That(acknowledged.StandardOutput)
            .Contains("Fake (+1/-0 lines since " + ReleaseScriptProbe.LastTag)
            .Because(
                "the acknowledgement must print the delta it is overriding — an operator "
                    + "who cannot see what is being skipped is not acknowledging anything. "
                    + acknowledged.Explain
            );
        await Assert
            .That(probe.TagNames())
            .Contains("v" + ReleaseScriptProbe.StubNextVersion)
            .Because(
                "-ForceBaselineDrift is the explicit ack that lets the release proceed. "
                    + acknowledged.Explain
            );
    }

    /// <summary>
    /// Hole 2: a package that was released at the last tag and is no longer packed here is
    /// an API change, so the version rule must take its <c>x</c> branch. Here the project
    /// and its baseline both went away — a developer removing a package normally removes
    /// both, which is why a worktree-only check cannot see this.
    /// <para>
    /// On the pre-fix script nothing in the diff loop is even looking: it iterates the
    /// packages that exist now. The run prints "API changed : false" and asks for a <c>y</c>
    /// bump (<c>v2026.8.0</c>), the stub's answer for <c>--api-changed false</c>.
    /// </para>
    /// </summary>
    [Test]
    [SkipUnlessReleaseScriptHost]
    public async Task A_package_dropped_since_the_last_tag_is_an_api_change()
    {
        using var probe = ReleaseScriptProbe.Create();
        probe.AddPackableProject("Gone", withBaseline: true);
        probe.Commit("probe: a second package, released at the last tag");
        probe.Retag(ReleaseScriptProbe.LastTag);

        probe.DropPackableProject("Gone", alsoDeleteBaseline: true);
        probe.Commit("probe: drop the Gone package");

        var run = probe.Run();
        await Assert.That(run.ExitCode).IsEqualTo(0).Because(run.Explain);
        await Assert
            .That(run.StandardOutput)
            .Contains($"Gone: released at {ReleaseScriptProbe.LastTag} but nothing is packed here now")
            .Because(
                "the release has to name the package that disappeared, not just take an x "
                    + "bump for an unexplained reason. "
                    + run.Explain
            );
        await Assert
            .That(run.Decision())
            .Contains("API changed  : true")
            .Because(
                "a dropped package IS a public-API change; 'false' is the y bump that would "
                    + "ship it. "
                    + run.Explain
            );
        await Assert
            .That(run.Decision())
            .Contains($"next version : v{ReleaseScriptProbe.StubNextVersionWhenApiChanged}")
            .Because(
                "the version rule must be asked with --api-changed true, i.e. take the x "
                    + "branch (the stub answers "
                    + $"{ReleaseScriptProbe.StubNextVersionWhenApiChanged} only for true). "
                    + run.Explain
            );
    }

    /// <summary>
    /// Hole 2, the other shape: the stale baseline is left behind. Then the worktree still
    /// contains a <c>Gone.public.txt</c> that no packable project produces, and the release
    /// must still call it a breaking change — comparing the baseline SET at the last tag
    /// against the packages packed today catches both shapes, and comparing "the files in
    /// api/ now" against "the files at the tag" would catch neither one reliably.
    /// </summary>
    [Test]
    [SkipUnlessReleaseScriptHost]
    public async Task A_dropped_package_is_detected_even_when_its_stale_baseline_is_left_behind()
    {
        using var probe = ReleaseScriptProbe.Create();
        probe.AddPackableProject("Gone", withBaseline: true);
        probe.Commit("probe: a second package, released at the last tag");
        probe.Retag(ReleaseScriptProbe.LastTag);

        probe.DropPackableProject("Gone", alsoDeleteBaseline: false);
        probe.Commit("probe: drop the Gone project, keep its baseline");

        var run = probe.Run();
        await Assert.That(run.ExitCode).IsEqualTo(0).Because(run.Explain);
        await Assert
            .That(run.Decision())
            .Contains("API changed  : true")
            .Because(
                "an orphaned baseline is still a package a consumer can no longer get. "
                    + run.Explain
            );
        await Assert
            .That(run.Decision())
            .Contains($"next version : v{ReleaseScriptProbe.StubNextVersionWhenApiChanged}")
            .Because(run.Explain);
    }
}
