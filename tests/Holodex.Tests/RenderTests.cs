using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;
using Omniwand;
using Xunit;

namespace Holodex.Tests;

/// <summary>Draws the skill tree offscreen to a PNG (HOLODEX_RENDER=folder) so its look can be checked without the running app.</summary>
public class RenderTests
{
    [Fact]
    public void Skill_tree_renders()
    {
        string? folder = Environment.GetEnvironmentVariable("HOLODEX_RENDER");
        if (string.IsNullOrEmpty(folder)) return;
        Exception? error = null;
        var t = new Thread(() =>
        {
            try
            {
                PhosphorFonts.EnsureLoaded();
                var ledger = new ProgressionLedger(Path.Combine(Path.GetTempPath(), $"render-{Guid.NewGuid():N}.json"));
                ledger.SetApexEvaluation(new ApexEvaluation
                {
                    BaselineLevels = new Dictionary<Skill, int> { [Skill.Art] = 42, [Skill.Craft] = 61, [Skill.Code] = 33, [Skill.Business] = 24, [Skill.Social] = 18, [Skill.Gaming] = 27, [Skill.Fitness] = 12, [Skill.Cooking] = 9, [Skill.Learning] = 30, [Skill.Academics] = 15 },
                    Traits = new Dictionary<string, int> { ["focus"] = 72, ["consistency"] = 55, ["curiosity"] = 88 }
                });
                ledger.Award("user", ProgressTrack.Human, Skill.Art, new XpAwardInput(3, new QualityComponents(0.9, 0.8, 0.7, 0.6, 1), 0.9, 1), EvidenceKind.Trial, "gauntlet", "watcher");
                var tree = new SkillTreeView { Size = new Size(500, 360) };
                var traits = new TraitBars { Size = new Size(500, 64) };
                tree.CreateControl();
                traits.CreateControl();
                tree.Show(ledger.Standings("user"), ledger.OverallLevel("user"), "YOU");
                traits.Show(ledger.Traits("user"));
                using var bmp = new Bitmap(500, 424);
                tree.DrawToBitmap(bmp, new Rectangle(0, 0, 500, 360));
                using (var part = new Bitmap(500, 64))
                {
                    traits.DrawToBitmap(part, new Rectangle(0, 0, 500, 64));
                    using var g = Graphics.FromImage(bmp);
                    g.DrawImage(part, 0, 360);
                }
                Directory.CreateDirectory(folder);
                bmp.Save(Path.Combine(folder, "skill-tree.png"), ImageFormat.Png);
            }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error != null) throw error;
    }

    [Fact]
    public void Apex_and_gauntlet_render()
    {
        string? folder = Environment.GetEnvironmentVariable("HOLODEX_RENDER");
        if (string.IsNullOrEmpty(folder)) return;
        Exception? error = null;
        bool saved = false;
        var t = new Thread(() =>
        {
            try
            {
                PhosphorFonts.EnsureLoaded();
                Directory.CreateDirectory(folder);
                // The forms only read the service when evaluating or starting; a render needs none.
                foreach (Form form in new Form[] { new ApexEvaluationForm(null!, () => { }), new GauntletForm(null!, QuestBoard.Starters().First(q => q.Kind == QuestKind.Gauntlet)) })
                {
                    form.StartPosition = FormStartPosition.Manual;
                    form.Location = new Point(-4000, -4000);
                    form.Show();
                    Application.DoEvents();
                    Application.DoEvents();
                    using var bmp = new Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
                    bmp.Save(Path.Combine(folder, form is ApexEvaluationForm ? "apex-evaluation.png" : "gauntlet.png"), ImageFormat.Png);
                    form.Hide();
                }
                saved = true;
            }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
        // The images are the result; a thread that still holds windows may not end promptly, so wait on them.
        for (int i = 0; i < 600 && !saved && error == null; i++) Thread.Sleep(100);
        if (error != null) throw error;
        Assert.True(saved, "the renders were saved");
    }
}
