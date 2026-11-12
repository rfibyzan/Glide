using System.Text;
using Glide.Core.Search;
using Glide.Core.Settings;
using Xunit;

namespace Glide.Core.Tests;

public sealed class PriorityTests
{
    [Fact]
    public void StartMenuShortcuts_ReceiveNormalPriorityBoost()
    {
        string startMenuApp = @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Visual Studio Code.lnk";
        int adj = PriorityEvaluator.GetScoreAdjustment(startMenuApp, "Visual Studio Code.lnk");
        Assert.Equal(PriorityEvaluator.NormalPriorityBoost, adj);
    }

    [Fact]
    public void DotfilesAndTempFiles_AreDemoted()
    {
        int dotfileAdj = PriorityEvaluator.GetScoreAdjustment(@"D:\Projects\Repo\.git\config", "config");
        // Dotfile in name
        int dotNameAdj = PriorityEvaluator.GetScoreAdjustment(@"D:\Projects\Repo\.gitignore", ".gitignore");
        int tempAdj = PriorityEvaluator.GetScoreAdjustment(@"D:\Docs\~$Proposal.docx", "~$Proposal.docx");
        int dollarAdj = PriorityEvaluator.GetScoreAdjustment(@"C:\$SysReset\boot.sdi", "$SysReset");

        Assert.Equal(PriorityEvaluator.RarelyUsedPenalty, dotNameAdj);
        Assert.Equal(PriorityEvaluator.RarelyUsedPenalty, tempAdj);
        Assert.Equal(PriorityEvaluator.RarelyUsedPenalty, dollarAdj);
    }

    [Fact]
    public void NodeModules_AreDemoted()
    {
        string nodeModule = @"D:\Projects\App\node_modules\lodash\index.js";
        int adj = PriorityEvaluator.GetScoreAdjustment(nodeModule, "index.js");
        Assert.Equal(PriorityEvaluator.RarelyUsedPenalty, adj);

        byte[] bytes = Encoding.UTF8.GetBytes(nodeModule);
        byte[] nameBytes = "index.js"u8.ToArray();
        int byteAdj = PriorityEvaluator.GetScoreAdjustmentBytes(bytes, nameBytes);
        Assert.Equal(PriorityEvaluator.RarelyUsedPenalty, byteAdj);
    }

    [Fact]
    public void RecycleBin_IsExcluded()
    {
        string recycle = @"C:\$Recycle.Bin\S-1-5-21\deleted.txt";
        Assert.True(PriorityEvaluator.IsExcluded(recycle));

        byte[] bytes = Encoding.UTF8.GetBytes(recycle);
        Assert.True(PriorityEvaluator.IsExcludedBytes(bytes));
    }

    [Fact]
    public void RarelyUsedPaths_AreDemoted()
    {
        string winOld = @"C:\Windows.old\Program Files\app.exe";
        int adj = PriorityEvaluator.GetScoreAdjustment(winOld, "app.exe");
        Assert.Equal(PriorityEvaluator.RarelyUsedPenalty, adj);
    }

    [Fact]
    public void NormalUserFiles_HaveNeutralAdjustment()
    {
        string userCode = @"D:\Projects\App\src\main.py";
        int adj = PriorityEvaluator.GetScoreAdjustment(userCode, "main.py");
        Assert.Equal(0, adj);
    }
}

