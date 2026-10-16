using System.Collections.Generic;
using Renegade.Ui;

namespace CodeBreaker.Game;

/// <summary>The guided tour. Clips are recorded from the game with --clip (see Main.PlayClip).</summary>
public static class Tutorial
{
	public static readonly IReadOnlyList<TutorialPage> Pages = new[]
	{
		new TutorialPage("Crack the code",
			"A secret row of four coloured pegs hides under the cover at the far end. You have twelve guesses to work it out.",
			"res://tutorial/guess.ogv"),
		new TutorialPage("Make a guess",
			"Tap colours in the tray to fill the glowing row, left to right. Tap a peg in the row to take it out again. When all four holes are full, press Check.",
			"res://tutorial/guess.ogv"),
		new TutorialPage("Read the clues",
			"Small pegs beside your guess are the clues. Red: a right colour in the right place. White: a right colour in the wrong place. They don't say which peg is which; that's for you to work out.",
			"res://tutorial/crack.ogv"),
		new TutorialPage("Levels",
			"Easy uses 4 colours, never repeated. Medium uses 6, and a colour can appear more than once. Hard adds a seventh colour and the blank: a faint, empty peg that counts as a colour of its own.",
			"res://tutorial/blank.ogv"),
		new TutorialPage("Cracked!",
			"Get all four red and the cover lifts: the code catches fire. Stuck? Hint (ad) shows you one peg of the code.",
			"res://tutorial/crack.ogv"),
	};
}
