namespace Omniwand;

/// <summary>
/// Input Studio in Holodex: the cursor designer (shape, look, morph, transitions, preview, install) and the input
/// bindings page, which the Input Studio service in Ark (apps/input-studio) serves on the Input Spine port. Holodex
/// opens it in its own window; the service keeps the parts a page cannot do (writing the .cur/.ani files and the
/// Windows cursor scheme) and stays the Input Spine that Spells, the Stream Deck and the AHK bridge send to.
/// </summary>
internal sealed class InputStudioForm : ArkPageForm
{
    public InputStudioForm()
        : base("InputStudio", "Input Studio", "HOLODEX // INPUT STUDIO // CURSOR AND BINDINGS", PhosphorTheme.Moss, new Size(1320, 860)) { }

    protected override string PageUrl => ArkStationClient.DefaultInputSpineUrl + "/";

    protected override (string Service, string Explanation) FailureText =>
        ("INPUT STUDIO", "The cursor designer is served by the Input Studio service in Ark (apps/input-studio), which the Ark runtime keeps running; start the runtime and try again.");

    /// <summary>Offered on an Ark install; on a public install there is no Input Studio service to open.</summary>
    public static bool Available(ArkStationClient ark) => ark.ArkInstalled;

    public async void Summon(Point cursorPos) => await PresentAsync(cursorPos);
}
