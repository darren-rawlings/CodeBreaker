using System.Linq;
using CodeBreaker.Core;
using Renegade.Core;
using Xunit;

namespace CodeBreaker.Tests;

public class CodeTests
{
	[Theory]
	[InlineData(new[] { 0, 1, 2, 3 }, new[] { 0, 1, 2, 3 }, 4, 0)]
	[InlineData(new[] { 0, 1, 2, 3 }, new[] { 3, 2, 1, 0 }, 0, 4)]
	[InlineData(new[] { 0, 0, 1, 1 }, new[] { 0, 1, 0, 2 }, 1, 2)]
	[InlineData(new[] { 0, 0, 0, 1 }, new[] { 0, 1, 1, 1 }, 2, 0)]
	[InlineData(new[] { 5, 5, 5, 5 }, new[] { 0, 1, 2, 3 }, 0, 0)]
	public void ScoringCountsEachPegOnce(int[] secret, int[] guess, int exact, int near)
	{
		Assert.Equal(new Feedback(exact, near), CodeGame.Score(secret, guess));
	}

	[Fact]
	public void SolvingEndsTheGame()
	{
		var game = new CodeGame(Level.For(Difficulty.Medium), new[] { 1, 2, 3, 4 });
		game.Guess(new[] { 0, 0, 0, 0 });
		Assert.False(game.IsOver);
		game.Guess(new[] { 1, 2, 3, 4 });
		Assert.True(game.IsSolved);
		Assert.True(game.IsOver);
	}

	[Fact]
	public void TwelveWrongGuessesLose()
	{
		var game = new CodeGame(Level.For(Difficulty.Medium), new[] { 1, 2, 3, 4 });
		for (int i = 0; i < 11; i++)
			game.Guess(new[] { 0, 0, 0, 0 });
		Assert.False(game.IsOver);
		game.Guess(new[] { 0, 0, 0, 0 });
		Assert.True(game.IsOver);
		Assert.False(game.IsSolved);
	}

	[Fact]
	public void EasyCodesNeverRepeatAColourAndRejectRepeatedGuesses()
	{
		for (int seed = 0; seed < 50; seed++)
		{
			var game = new CodeGame(Level.For(Difficulty.Easy), seed);
			Assert.Equal(4, game.Secret.Distinct().Count());
		}
		Assert.False(new CodeGame(Level.For(Difficulty.Easy), 1).IsValid(new[] { 0, 0, 1, 2 }));
	}

	[Theory]
	[InlineData(Difficulty.Easy, 4, false)]
	[InlineData(Difficulty.Medium, 6, false)]
	[InlineData(Difficulty.Hard, 8, true)]
	public void LevelsHaveFourPegsTwelveRowsAndMoreColours(Difficulty difficulty, int colours, bool blank)
	{
		var level = Level.For(difficulty);
		Assert.Equal(4, level.Pegs);
		Assert.Equal(12, level.Guesses);
		Assert.Equal(colours, level.Colours);
		Assert.Equal(blank, level.Blank);
	}

	[Fact]
	public void TheBlankScoresLikeAColour()
	{
		var level = Level.For(Difficulty.Hard);
		int blank = level.BlankColour;
		Assert.Equal(7, blank);
		var game = new CodeGame(level, new[] { blank, 1, blank, 2 });
		Assert.True(game.IsValid(new[] { blank, blank, blank, blank }));
		Assert.Equal(new Feedback(1, 1), game.Guess(new[] { blank, blank, 0, 0 }));
		Assert.Equal(new Feedback(0, 2), CodeGame.Score(game.Secret, new[] { 0, blank, 0, blank }));
	}

	[Fact]
	public void HintsRevealEachPegOnce()
	{
		var game = new CodeGame(Level.For(Difficulty.Hard), 3);
		var shown = Enumerable.Range(0, 4).Select(_ => game.RevealHint()).ToArray();
		Assert.Equal(new[] { 0, 1, 2, 3 }, shown);
		Assert.Equal(-1, game.RevealHint());
	}
}
