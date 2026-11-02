using StoryForge.Engine.Clips;
using StoryForge.Engine.Stills;

namespace StoryForge.Engine.Tests;

/// <summary>What the still prompt writer is told, and what is sent back to it.</summary>
public sealed class StillPromptTests
{
    private static StillShot Shot(string label, IReadOnlyList<StillPicture>? pictures = null, bool fromBefore = false, IReadOnlyList<StillPicture>? described = null) =>
        new(label, "Trolls break through a wall.", ["Trolls"], "", pictures ?? [], fromBefore, described ?? []);

    private static string Prompt(params StillShot[] shots) =>
        ClaudeCliStillPromptAgent.FirstPrompt(new StillPromptRequest(Guid.NewGuid(), "A lore video.", shots, "", ""));

    private static IReadOnlyList<string> Problems(StillShot shot, string prompt) =>
        StillPromptCheck.Problems(new StillPromptsOutput([new StillPromptOutput(StillPromptCheck.Name(shot.Label), prompt)]), [shot]);

    private static readonly StillPicture Troll = new("Trolls", "Hulking and grey.");

    [Fact]
    public void The_clip_prompt_gets_the_cast_and_their_looks()
    {
        var prompt = ClaudeCliClipPromptAgent.FirstPrompt(new ClipPromptRequest(Guid.NewGuid(), "A lore video.",
            [new ClipShot("S02\u00b71", "Nosulgor sleeps.", "", "the still", 5, [new StillPicture("Nosulgor", "An ancient dragon, blind: no eyes visible.")])], "", ""));

        Assert.Contains("In it: Nosulgor: An ancient dragon, blind: no eyes visible.", prompt);
    }

    [Fact]
    public void The_clip_rules_ask_to_name_what_must_not_change()
    {
        var prompt = ClaudeCliClipPromptAgent.FirstPrompt(new ClipPromptRequest(Guid.NewGuid(), "A lore video.",
            [new ClipShot("S02\u00b71", "Nosulgor sleeps.", "", "the still", 5, [])], "", ""));

        Assert.Contains("Name what must not change", prompt);
        Assert.Contains("his eye sockets stay empty", prompt);
    }

    [Theory]
    [InlineData("the dragon from picture 1")]
    [InlineData("the dragon from Picture 1, in a cave")]
    [InlineData("pictures 1 and 2 together")]
    public void A_prompt_naming_a_picture_when_none_is_sent_is_sent_back(string prompt) =>
        Assert.Contains("S01-1 names picture 1, but this shot comes with no pictures: describe the subject in words.", Problems(Shot("S01·1"), prompt));

    [Fact]
    public void A_prompt_naming_a_picture_beyond_those_sent_is_sent_back() =>
        Assert.Contains("S01-1 names picture 3, but this shot comes with only 2: describe the subject in words.", Problems(Shot("S01·1", [Troll, Troll]), "Picture 3 shows it."));

    [Fact]
    public void A_prompt_naming_a_picture_that_is_sent_passes() =>
        Assert.Empty(Problems(Shot("S01·1", [Troll, Troll]), "The trolls from picture 2."));

    [Fact]
    public void A_picture_of_a_thing_is_not_a_picture_number() =>
        Assert.Empty(Problems(Shot("S01·1"), "A framed picture of 3 trolls hangs on the wall, 12 picture frames beside it."));

    [Fact]
    public void An_earlier_still_says_to_take_only_the_subject()
    {
        var prompt = Prompt(Shot("S03·2", [new StillPicture("Trolls", "Hulking and grey.", "S03-1")]));

        Assert.Contains("Picture 1 is the still of S03-1: take Trolls from it, not its surroundings. Trolls: Hulking and grey.", prompt);
    }

    [Fact]
    public void An_earlier_still_of_a_place_keeps_its_surroundings()
    {
        var prompt = Prompt(Shot("S03·2", [new StillPicture("The vault", "A low stone vault.", "S03-1", KeepSurroundings: true)]));

        Assert.Contains("Picture 1 is the still of S03-1: The vault, with its surroundings. The vault: A low stone vault.", prompt);
    }

    [Fact]
    public void A_look_is_given_for_a_reference_picture_too() =>
        Assert.Contains("Picture 1 shows Trolls: Hulking and grey.", Prompt(Shot("S03·2", [Troll])));

    [Fact]
    public void Someone_in_no_picture_is_described_from_their_look() =>
        Assert.Contains("In no picture, so describe how they look: Trolls: Hulking and grey.", Prompt(Shot("S03·2", described: [Troll])));

    [Fact]
    public void With_no_pictures_at_all_the_rules_never_mention_pictures()
    {
        var prompt = Prompt(Shot("S01·1", described: [Troll]));

        Assert.DoesNotContain("from picture", prompt);
        Assert.DoesNotContain("Picture 1", prompt);
    }

    [Fact]
    public void Size_and_fixed_traits_are_stated_in_words_even_with_a_picture()
    {
        var prompt = Prompt(Shot("S03·2", [Troll]));

        Assert.Contains("a picture does not carry size", prompt);
        Assert.Contains("from picture 1", prompt);
    }

    [Fact]
    public void The_system_prompt_does_not_promise_pictures() =>
        Assert.DoesNotContain("from your prompt and from the", ClaudeCliStillPromptAgent.SystemPrompt);
}
