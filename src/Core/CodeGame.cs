using System;
using System.Collections.Generic;
using System.Linq;
using Renegade.Core;

namespace CodeBreaker.Core;

/// <summary>How a guess scored: exact (right colour, right place) and near (right colour, wrong place).</summary>
public readonly record struct Feedback(int Exact, int Near);

/// <summary>A level's shape: how many pegs, how many colours, and whether a colour may repeat.</summary>
public sealed record Level(int Pegs, int Colours, bool Repeats, int Guesses = 10)
{
	public static Level For(Difficulty difficulty) => difficulty switch
	{
		Difficulty.Easy => new(4, 6, Repeats: false),
		Difficulty.Medium => new(4, 6, Repeats: true),
		_ => new(5, 8, Repeats: true),
	};
}

/// <summary>
/// Code Breaker (the classic code-guessing game): the computer hides a code of coloured pegs; you
/// guess, and each guess is scored with exact and near pegs, until you crack it or run out of
/// guesses. A hint (paid for with a rewarded ad) reveals one peg of the code.
/// </summary>
public sealed class CodeGame
{
	private readonly List<(int[] Guess, Feedback Score)> _guesses = new();
	private readonly HashSet<int> _revealed = new();

	public CodeGame(Level level, int? seed = null)
	{
		Level = level;
		var random = seed is { } s ? new Random(s) : new Random();
		Secret = MakeSecret(level, random);
	}

	/// <summary>For tests: a game with a known code.</summary>
	public CodeGame(Level level, int[] secret)
	{
		Level = level;
		Secret = (int[])secret.Clone();
	}

	public Level Level { get; }
	public int[] Secret { get; }

	public IReadOnlyList<(int[] Guess, Feedback Score)> Guesses => _guesses;

	/// <summary>Peg positions the player has been shown as hints.</summary>
	public IReadOnlyCollection<int> Revealed => _revealed;

	public bool IsSolved => _guesses.Count > 0 && _guesses[^1].Score.Exact == Level.Pegs;

	public bool IsOver => IsSolved || _guesses.Count >= Level.Guesses;

	public int GuessesLeft => Level.Guesses - _guesses.Count;

	public static Feedback Score(int[] secret, int[] guess)
	{
		int exact = 0;
		var secretLeft = new int[16];
		var guessLeft = new int[16];
		for (int i = 0; i < secret.Length; i++)
		{
			if (secret[i] == guess[i])
			{
				exact++;
			}
			else
			{
				secretLeft[secret[i]]++;
				guessLeft[guess[i]]++;
			}
		}
		int near = 0;
		for (int colour = 0; colour < 16; colour++)
			near += Math.Min(secretLeft[colour], guessLeft[colour]);
		return new Feedback(exact, near);
	}

	public bool IsValid(int[] guess) =>
		!IsOver && guess.Length == Level.Pegs && guess.All(c => c >= 0 && c < Level.Colours)
		&& (Level.Repeats || guess.Distinct().Count() == guess.Length);

	public Feedback Guess(int[] guess)
	{
		if (!IsValid(guess))
			throw new InvalidOperationException("Not a valid guess.");
		var score = Score(Secret, guess);
		_guesses.Add(((int[])guess.Clone(), score));
		return score;
	}

	/// <summary>Reveals a peg the player hasn't been shown yet (left to right); -1 if all are shown.</summary>
	public int RevealHint()
	{
		for (int i = 0; i < Level.Pegs; i++)
		{
			if (_revealed.Add(i))
				return i;
		}
		return -1;
	}

	private static int[] MakeSecret(Level level, Random random)
	{
		if (level.Repeats)
			return Enumerable.Range(0, level.Pegs).Select(_ => random.Next(level.Colours)).ToArray();
		return Enumerable.Range(0, level.Colours).OrderBy(_ => random.Next()).Take(level.Pegs).ToArray();
	}
}
