using StoryForge.Client;
using StoryForge.Engine.Assembly;

namespace StoryForge.Engine.Tests;

/// <summary>
/// The cut list against the clips' handles: a cut moves only inside them, a transition takes half
/// its length from each clip, the sound's J/L too; and the render counts in whole frames that add up
/// to the narration.
/// </summary>
public sealed class CutPlanTests
{
    /// <summary>Three shots of 4 s, 3 s and 5 s; each clip has 0.5 s before and after its shot.</summary>
    private static Timeline Three(double handle = 0.5) => new(
        [Shot("a", "S01·1", 0, 4, handle), Shot("b", "S01·2", 4, 7, handle), Shot("c", "S02·1", 7, 12, handle)],
        [("voice/S01.wav", 7), ("voice/S02.wav", 5)]);

    private static CutShot Shot(string key, string label, double start, double end, double handle) =>
        new(key, label, label[..3], start, end, start - handle, end + handle, $"clips/{key}.mp4", 1, $"shot {key}", "");

    private static CutList Cuts(params Cut[] cuts) => new(cuts, CutList.DefaultClipSoundDb, []);

    private static Cut Hard(string after, double shift = 0, double lead = 0) => new(after, CutTransition.Cut, 0, shift, lead, "");

    [Fact]
    public void Hard_cuts_on_the_words_fit()
    {
        Assert.Empty(CutPlan.Problems(Cuts(Hard("a"), Hard("b")), Three()));
    }

    [Theory]
    [InlineData(0.4, 0.2, "S01·1 → S01·2: a 0.4 s crossfade 0.2 s later needs 0.4 s of S01·1's clip after its words; it has 0.5 s.")]
    [InlineData(0.6, 0.3, "S01·1 → S01·2: a 0.6 s crossfade 0.3 s later needs 0.6 s of S01·1's clip after its words; it has 0.5 s.")]
    [InlineData(0.8, -0.2, "S01·1 → S01·2: a 0.8 s crossfade 0.2 s earlier needs 0.6 s of S01·2's clip before its words; it has 0.5 s.")]
    public void A_transition_takes_half_its_length_from_each_clip_and_the_shift_moves_it(double seconds, double shift, string problem)
    {
        var problems = CutPlan.Problems(Cuts(new Cut("a", CutTransition.Crossfade, seconds, shift, 0, ""), Hard("b")), Three());

        if (seconds == 0.4)
        {
            Assert.Empty(problems);   // 0.2 + 0.2 = 0.4: inside the 0.5 s
        }
        else
        {
            Assert.Equal(problem, Assert.Single(problems));
        }
    }

    [Fact]
    public void The_sound_changing_before_the_picture_must_fit_the_next_clips_handle()
    {
        Assert.Empty(CutPlan.Problems(Cuts(Hard("a", lead: 0.4), Hard("b")), Three()));

        var problems = CutPlan.Problems(Cuts(Hard("a", shift: -0.3, lead: 0.3), Hard("b")), Three());

        Assert.Equal("S01·1 → S01·2: the sound changing 0.3 s before the picture runs past the clips' handles; make it change closer to the picture.", Assert.Single(problems));
    }

    [Fact]
    public void Every_shot_stays_on_screen_alone_for_a_while()
    {
        // b lasts 0.6 s: a 0.4 s crossfade at each end takes 0.2 s of it, which leaves it 0.2 s alone.
        var timeline = new Timeline([Shot("a", "S01·1", 0, 4, 0.5), Shot("b", "S01·2", 4, 4.6, 0.5), Shot("c", "S01·3", 4.6, 9, 0.5)], [("voice/S01.wav", 9)]);

        var problems = CutPlan.Problems(Cuts(new Cut("a", CutTransition.Crossfade, 0.4, 0, 0, ""), new Cut("b", CutTransition.Crossfade, 0.4, 0, 0, "")), timeline);

        Assert.Equal("S01·2 is on screen alone for only 0.2 s between its transitions; give it at least 0.3 s.", Assert.Single(problems));
    }

    [Fact]
    public void A_list_for_other_shots_or_out_of_range_values_says_what_is_wrong()
    {
        Assert.Equal("There are 3 shots, so 2 cuts: one after every shot but the last. The list has 1.", Assert.Single(CutPlan.Problems(Cuts(Hard("a")), Three())));
        Assert.Equal("Cut 2 should come after S01·2.", Assert.Single(CutPlan.Problems(Cuts(Hard("a"), Hard("c")), Three())));
        Assert.Equal("S01·1 → S01·2: a crossfade lasts 0.2 to 1 s, not 1.5.",
            Assert.Single(CutPlan.Problems(Cuts(new Cut("a", CutTransition.Crossfade, 1.5, 0, 0, ""), Hard("b")), Three())));
        Assert.Equal("The clips' sound goes from -60 dB to 0 dB, not 3.",
            Assert.Single(CutPlan.Problems(Cuts(Hard("a"), Hard("b")) with { ClipSoundDb = 3 }, Three())));
    }

    [Fact]
    public void The_render_counts_whole_frames_that_add_up_to_the_narration()
    {
        // A 0.4 s crossfade on the words after a, a hard cut 0.2 s early after b.
        var plan = CutPlan.Render(Cuts(new Cut("a", CutTransition.Crossfade, 0.4, 0, 0, ""), Hard("b", shift: -0.2)), Three(), new GenerationSize("16:9", 1920, 1080), 24);

        // a: 0 → 4.2 s (its clip starts 0.5 s before), b: 3.8 → 6.8 s, c: 6.8 → 12 s.
        Assert.Equal(new PicturePart("clips/a.mp4", 12, 101), plan.Pictures[0]);
        Assert.Equal(new PicturePart("clips/b.mp4", 7, 72), plan.Pictures[1]);
        Assert.Equal(new PicturePart("clips/c.mp4", 7, 125), plan.Pictures[2]);
        Assert.Equal([(CutTransition.Crossfade, 10), (CutTransition.Cut, 0)], plan.Joins);
        // The frames of the joined video: every piece, less the overlaps, is the narration's 12 s.
        Assert.Equal(12 * 24, plan.Pictures.Sum(p => p.Frames) - plan.Joins.Sum(j => j.Frames));
        Assert.Equal(["voice/S01.wav", "voice/S02.wav"], plan.Narration);
    }

    [Fact]
    public void The_clips_sound_is_placed_on_the_timeline_with_its_J_cut_and_faded_at_the_cuts()
    {
        var plan = CutPlan.Render(Cuts(Hard("a"), Hard("b", lead: 0.3)), Three(), new GenerationSize("16:9", 1920, 1080), 24);

        // b's sound: from its cut in (4 s, a 0.04 s fade around it) to 0.3 s before its picture cut out at 7 s.
        Assert.Equal(new SoundPart("clips/b.mp4", 0.48, 3.22, 3.98, 0.04, 0.04), plan.Sounds[1]);
        // c's sound starts 0.3 s before its picture, at 6.7 s.
        Assert.Equal(new SoundPart("clips/c.mp4", 0.18, 5.5, 6.68, 0.04, 0), plan.Sounds[2]);
        Assert.Equal(new SoundPart("clips/a.mp4", 0.5, 4.52, 0, 0, 0.04), plan.Sounds[0]);
    }
}
