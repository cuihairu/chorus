using System.Text;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// The FIX-WEB-05 drift gate: the committed console contract file must equal a fresh render
/// from the compiled descriptors. A proto edit without regeneration (or a hand edit of the
/// generated file) fails here, so the console's typed client cannot drift from the Admin
/// contract it speaks. CHORUS_REGEN_ADMIN_TS=1 turns the gate into the regenerator for the
/// local developer run — CI only ever compares.
/// </summary>
public sealed class RedesignWeb05AdminClientContractTests
{
    [Fact]
    public void TheGeneratedAdminClientMatchesTheCommittedContractTs()
    {
        var rendered = AdminTsContract.Render();
        var path = AdminTsContract.FindCommittedPath();

        if (Environment.GetEnvironmentVariable("CHORUS_REGEN_ADMIN_TS") == "1")
        {
            File.WriteAllText(path, rendered.ReplaceLineEndings("\n"), new UTF8Encoding(false));
            return;
        }

        var committed = File.ReadAllText(path, Encoding.UTF8);
        Assert.True(committed == rendered, Difference(committed, rendered));
    }

    private static string Difference(string committed, string rendered)
    {
        var committedLines = committed.Split('\n');
        var renderedLines = rendered.Split('\n');
        var first = 0;
        while (first < committedLines.Length && first < renderedLines.Length
               && committedLines[first] == renderedLines[first])
        {
            first++;
        }

        return
            $"console/src/api/contract.ts drifted from the descriptors (regenerate with CHORUS_REGEN_ADMIN_TS=1)." +
            $"\nfirst differing line {first + 1}:" +
            $"\n  committed: {(first < committedLines.Length ? committedLines[first] : "<end>")}" +
            $"\n  rendered:  {(first < renderedLines.Length ? renderedLines[first] : "<end>")}";
    }
}
