using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CodeBreaker.Core;
using Godot;
using Renegade.Ads;
using Renegade.Audio;
using Renegade.Core;
using Renegade.Dev;
using Renegade.Looks;
using Renegade.Play;
using Renegade.Ui;

namespace CodeBreaker.Game;

/// <summary>
/// Code Breaker. Tap colours in the tray to fill the glowing row, then Check: red pegs mean right
/// colour in the right place, white right colour in the wrong place. Crack the code and the
/// cover over the secret lifts away in a burst of sparks (the signature "reveal"). A hint, paid for
/// with a rewarded ad, shows one peg of the code. Drag sideways to spin the board.
/// </summary>
public partial class Main : Node3D
{
	public const string Title = "Code Breaker";

	/// <summary>Google's test ad units until the real ones exist in AdMob (see the release playbook).</summary>
	private static readonly AdMobConfig AdUnits = AdMobConfig.Test;

	private const float Pitch = 0.82f;
	private const float RowPitch = 0.72f;

	/// <summary>The strip in front of the first row that holds the key-peg legend.</summary>
	private const float LegendDepth = 1.0f;
	private const float PegRadius = 0.29f;
	private const float KeyRadius = 0.1f;
	private const float Thickness = 0.4f;

	/// <summary>
	/// The peg colours, in order; levels use the first 4, 6 or 8. The eighth is the blank (Hard
	/// only): it shows as a faint, smoky ghost of a peg, an "empty" hole that still counts.
	/// </summary>
	private static readonly Color[] PegColours =
	{
		new("ef4444"), new("3b82f6"), new("facc15"), new("22c55e"), new("a855f7"), new("f97316"), new("ec4899"), new("d6d3d1"),
	};

	private static readonly string[] ColourNames = { "red", "blue", "yellow", "green", "purple", "orange", "pink", "blank" };

	/// <summary>Key pegs: red for right colour in the right place, white for right colour in the wrong place.</summary>
	private static readonly Color ExactColour = new("dc2626");
	private static readonly Color NearColour = new("f5f5f4");

	private readonly List<Node3D> _boardNodes = new();
	private readonly List<MeshInstance3D> _trayPegs = new();
	private readonly MeshInstance3D?[] _current = new MeshInstance3D?[5];
	private readonly ImpactBank<Style> _impacts = new(Styles.Impact);

	private GameSettings _settings = null!;
	private CodeGame _game = null!;
	private Node3D _rig = null!;
	private Node3D _tray = null!;
	private Camera3D _camera = null!;
	private SoundPool _sound = null!;
	private GameHud _hud = null!;
	private IAdService _ads = null!;
	private AdSchedule? _adSchedule;
	private SpinControl _spin = null!;
	private Button _check = null!;
	private Node3D _cover = null!;
	private MeshInstance3D _slab = null!;
	private MeshInstance3D _rowGlow = null!;
	private readonly List<MeshInstance3D> _secretPegs = new();
	private int _solved;
	private int _played;
	private bool _busy;

	private bool _demo;
	private bool _quitAfterDemo;
	private string? _clip;
	private float _initialSpin;
	private float _demoTimer;

	private Level CurrentLevel => Level.For(_settings.Difficulty);

	private float BoardWidth => CurrentLevel.Pegs * Pitch + 1.5f;

	private float BoardLength => CurrentLevel.Guesses * RowPitch + 1.9f + LegendDepth;

	public override void _Ready()
	{
		DevLog.Tag = "cbr";
		AudioWatch.Attach(this, Title);
		_settings = GameSettings.Load(board: Style.Wood, pieces: Style.Glass);
		ReadDeveloperArgs();
		AudioServer.SetBusMute(0, _settings.Muted);

		_camera = new Camera3D { Position = new Vector3(0, 15.2f, 8.4f), Fov = 58 };
		AddChild(_camera);
		_camera.LookAt(new Vector3(0, 0f, 1.95f));
		Stage.Build(this, -Thickness, new Color("1a0f09"));
		_rig = new Node3D();
		AddChild(_rig);
		_rig.RotateY(_initialSpin);
		_tray = new Node3D();
		AddChild(_tray);
		Stage.WarmUp(this, -Thickness, Sparkle.Halo);

		_sound = new SoundPool();
		AddChild(_sound);
		_adSchedule ??= new AdSchedule(_settings.GamesSinceAd);
		var theme = HudTheme.Warm;
		_ads = AdServices.Create(this, AdUnits, _demo, new PlaceholderAds
		{
			TextColor = theme.Text,
			RewardTitle = "Watch for a hint",
			ClaimText = "Show me a peg",
			SkipText = "Skip (no hint)",
		});

		_hud = new GameHud(theme);
		AddChild(_hud);
		_hud.Build(new HudOptions(Title, "Tap colours to fill the row, then Check", PiecesLabel: "Pegs",
			PieceName: s => s switch { Style.Plastic => "Plastic", Style.Wood => "Wood", Style.Metal => "Metal", Style.Glass => "Glass", _ => "Marble" },
			UndoText: "Hint (ad)", LevelOnly: true), _settings);
		_hud.NewGamePressed += OnNewGamePressed;
		_hud.UndoPressed += OnHintPressed;
		_hud.RestartPressed += OnRestartPressed;
		_hud.SetTutorial(Tutorial.Pages);
		_hud.ModeChosen += (_, difficulty) =>
		{
			_settings.Difficulty = difficulty;
			_settings.Save();
			StartGame();
		};
		_hud.BoardChosen += style =>
		{
			_settings.Board = style;
			_settings.Save();
			_slab.MaterialOverride = Materials.For(Styles.BoardLook(style));
			_cover.GetChild<MeshInstance3D>(0).MaterialOverride = Materials.For(Styles.BoardLook(style));
		};
		_hud.PiecesChosen += style =>
		{
			_settings.Pieces = style;
			_settings.Save();
			BuildBoard();
		};

		// The Check button sits where New Game appears at the end of a game.
		_check = _hud.AddButton("Check", true, Control.LayoutPreset.CenterBottom, -150, 150, -192, -128, 30);
		_check.Visible = false;
		_check.Pressed += () => _ = Check();

		_spin = new SpinControl(_rig, () => !_busy && !_settings.FreezeBoard);
		_spin.Tapped += OnTapped;
		if (_clip != null)
		{
			_hud.Visible = false;
			_ = PlayClip(_clip);
			return;
		}
		StartGame();
		if (!_demo)
			_hud.OfferTutorialOnce(_settings);
		_hud.ApplyDevFlags();
	}

	// ---------------------------------------------------------------- Board

	/// <summary>Row 0 (your first guess) is nearest you; the secret sits at the far end.</summary>
	private float RowZ(int row) => BoardLength / 2 - 0.9f - LegendDepth - row * RowPitch;

	private float PegX(int index) => (index - (CurrentLevel.Pegs - 1) / 2f) * Pitch - 0.45f;

	private float SecretZ => -BoardLength / 2 + 0.65f;

	private Look PegLook(int colour)
	{
		var c = PegColours[colour];
		if (colour == CurrentLevel.BlankColour)
			return new Look(Look.GlassPattern, c, c.Darkened(0.3f), 0.1f, Opacity: 0.22f);
		return _settings.Pieces switch
		{
			Style.Glass => new Look(Look.GlassPattern, c, c.Lightened(0.4f), 0.04f, Opacity: 0.7f),
			Style.Metal => new Look(Look.MetalPattern, c, c.Darkened(0.35f), 0.25f, Metallic: 1f),
			Style.Marble => new Look(Look.MarblePattern, c, c.Darkened(0.4f), 0.15f, Scale: 2f),
			Style.Wood => new Look(Look.WoodPattern, c, c.Darkened(0.35f), 0.5f, Scale: 4f),
			_ => new Look(Look.PlasticPattern, c, c.Darkened(0.2f), 0.25f),
		};
	}

	private MeshInstance3D MakePeg(int colour, Node3D parent, Vector3 at, float radius = PegRadius)
	{
		var peg = new MeshInstance3D
		{
			Mesh = new SphereMesh { Radius = radius, Height = radius * 2, RadialSegments = 24, Rings = 12 },
			MaterialOverride = Materials.For(PegLook(colour)),
			Position = at,
		};
		parent.AddChild(peg);
		return peg;
	}

	/// <summary>Builds the board for the current level and lays out everything already played.</summary>
	private void BuildBoard()
	{
		foreach (var node in _boardNodes)
			node.QueueFree();
		_boardNodes.Clear();
		_secretPegs.Clear();
		System.Array.Clear(_current);

		var level = CurrentLevel;
		_slab = new MeshInstance3D
		{
			Mesh = new BoxMesh { Size = new Vector3(BoardWidth, Thickness, BoardLength) },
			Position = new Vector3(0, -Thickness / 2, 0),
			MaterialOverride = Materials.For(Styles.BoardLook(_settings.Board)),
		};
		Add(_slab);

		var hole = new CylinderMesh { TopRadius = PegRadius * 0.75f, BottomRadius = PegRadius * 0.75f, Height = 0.01f, RadialSegments = 20 };
		var keyHole = new CylinderMesh { TopRadius = KeyRadius * 0.9f, BottomRadius = KeyRadius * 0.9f, Height = 0.01f, RadialSegments = 12 };
		var dark = new StandardMaterial3D { AlbedoColor = new Color(0.05f, 0.02f, 0f, 0.6f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded };
		for (int row = 0; row < level.Guesses; row++)
		{
			for (int i = 0; i < level.Pegs; i++)
				Add(new MeshInstance3D { Mesh = hole, MaterialOverride = dark, Position = new Vector3(PegX(i), 0.006f, RowZ(row)) });
			for (int k = 0; k < level.Pegs; k++)
				Add(new MeshInstance3D { Mesh = keyHole, MaterialOverride = dark, Position = KeyPosition(row, k) with { Y = 0.006f } });
		}

		_rowGlow = new MeshInstance3D
		{
			Mesh = new PlaneMesh { Size = new Vector2(level.Pegs * Pitch + 0.2f, RowPitch * 0.9f) },
			MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(1f, 0.78f, 0.35f, 0.25f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded },
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
		};
		Add(_rowGlow);

		BuildLegend();

		// The secret, under a cover that lifts away at the end.
		for (int i = 0; i < level.Pegs; i++)
		{
			var peg = MakePeg(_game.Secret[i], _rig, new Vector3(PegX(i), PegRadius * 0.9f, SecretZ));
			_boardNodes.Add(peg);
			_secretPegs.Add(peg);
		}
		_cover = new Node3D { Position = new Vector3(PegX(0) + (level.Pegs - 1) * Pitch / 2, 0, SecretZ) };
		_cover.AddChild(new MeshInstance3D
		{
			Mesh = new BoxMesh { Size = new Vector3(level.Pegs * Pitch + 0.25f, 0.5f, 0.9f) },
			Position = new Vector3(0, 0.3f, 0),
			MaterialOverride = Materials.For(Styles.BoardLook(_settings.Board)),
		});
		Add(_cover);

		for (int row = 0; row < _game.Guesses.Count; row++)
		{
			var (guess, score) = _game.Guesses[row];
			for (int i = 0; i < guess.Length; i++)
				_boardNodes.Add(MakePeg(guess[i], _rig, new Vector3(PegX(i), PegRadius * 0.9f, RowZ(row))));
			int k = 0;
			for (int e = 0; e < score.Exact; e++)
				_boardNodes.Add(MakeKey(row, k++, exact: true));
			for (int n = 0; n < score.Near; n++)
				_boardNodes.Add(MakeKey(row, k++, exact: false));
		}

		foreach (var peg in _trayPegs)
			peg.QueueFree();
		_trayPegs.Clear();
		float trayZ = BoardLength / 2 + 0.75f;
		for (int colour = 0; colour < level.Colours; colour++)
		{
			float x = (colour - (level.Colours - 1) / 2f) * 0.78f;
			_trayPegs.Add(MakePeg(colour, _tray, new Vector3(x, PegRadius, trayZ)));
		}
		ShowHints();
		UpdateRowGlow();
	}

	private void Add(Node3D node)
	{
		_rig.AddChild(node);
		_boardNodes.Add(node);
	}

	/// <summary>
	/// The key to the clues, written on the board in front of the first row: a red key peg for
	/// "right colour, right place" and a white one for "right colour, wrong place".
	/// </summary>
	private void BuildLegend()
	{
		float front = BoardLength / 2 - LegendDepth / 2 - 0.12f;
		var lines = new[] { (true, "Right colour, right place"), (false, "Right colour, wrong place") };
		for (int i = 0; i < lines.Length; i++)
		{
			var (exact, text) = lines[i];
			float z = front + (i - 0.5f) * 0.42f;
			float left = -BoardWidth / 2 + 0.45f;
			var key = MakeKey(0, 0, exact);
			key.Position = new Vector3(left, KeyRadius, z);
			key.Scale = Vector3.One * 1.3f;
			_boardNodes.Add(key);
			Add(new Label3D
			{
				Text = text,
				FontSize = 80,
				PixelSize = 0.0042f,
				OutlineSize = 10,
				Modulate = new Color("fff3e0"),
				OutlineModulate = new Color(0.12f, 0.06f, 0.02f, 0.85f),
				HorizontalAlignment = HorizontalAlignment.Left,
				Position = new Vector3(left + 0.22f, 0.01f, z),
				RotationDegrees = new Vector3(-90, 0, 0),
				Shaded = false,
				DoubleSided = false,
			});
		}
	}

	/// <summary>Key pegs sit in a small grid to the right of each row.</summary>
	private Vector3 KeyPosition(int row, int index)
	{
		int perRow = (CurrentLevel.Pegs + 1) / 2;
		float x = BoardWidth / 2 - 0.62f + (index % perRow) * 0.22f - (perRow - 1) * 0.11f;
		float z = RowZ(row) + (index / perRow - 0.5f) * 0.24f;
		return new Vector3(x, KeyRadius, z);
	}

	private MeshInstance3D MakeKey(int row, int index, bool exact)
	{
		var c = exact ? ExactColour : NearColour;
		var look = new Look(Look.PlasticPattern, c, c.Darkened(0.25f), 0.3f);
		var key = new MeshInstance3D
		{
			Mesh = new SphereMesh { Radius = KeyRadius, Height = KeyRadius * 2, RadialSegments = 12, Rings = 6 },
			MaterialOverride = Materials.For(look),
			Position = KeyPosition(row, index),
		};
		_rig.AddChild(key);
		return key;
	}

	private void ShowHints()
	{
		foreach (int i in _game.Revealed)
		{
			if (_secretPegs.Count > i)
			{
				// A hinted peg rises up out of the cover so it can be seen.
				_secretPegs[i].Position = _secretPegs[i].Position with { Y = 0.95f };
				_secretPegs[i].MaterialOverlay = Sparkle.Halo;
			}
		}
	}

	private void UpdateRowGlow()
	{
		_rowGlow.Visible = !_game.IsOver;
		if (!_game.IsOver)
			_rowGlow.Position = new Vector3(PegX(0) + (CurrentLevel.Pegs - 1) * Pitch / 2, 0.004f, RowZ(_game.Guesses.Count));
	}

	// ---------------------------------------------------------------- Input

	public override void _UnhandledInput(InputEvent @event)
	{
		if (!_hud.MenuOpen)
			_spin.HandleInput(@event);
	}

	private void OnTapped(Vector2 screen)
	{
		if (_busy || _game.IsOver)
			return;
		var from = _camera.ProjectRayOrigin(screen);
		var direction = _camera.ProjectRayNormal(screen);
		if (direction.Y > -0.01f)
			return;
		var world = from + direction * ((PegRadius - from.Y) / direction.Y);
		// The tray first: tap a colour to put it in the next empty hole.
		for (int colour = 0; colour < _trayPegs.Count; colour++)
		{
			if ((_trayPegs[colour].GlobalPosition with { Y = world.Y } - world).Length() < 0.4f)
			{
				Place(colour);
				return;
			}
		}
		// Then the current row: tap a placed peg to take it out again.
		var local = _rig.ToLocal(world);
		if (Mathf.Abs(local.Z - RowZ(_game.Guesses.Count)) > RowPitch / 2)
			return;
		for (int i = 0; i < CurrentLevel.Pegs; i++)
		{
			if (_current[i] != null && Mathf.Abs(local.X - PegX(i)) < Pitch / 2)
			{
				_current[i]!.QueueFree();
				_current[i] = null;
				Click(0.4f);
				RefreshUi();
				return;
			}
		}
	}

	private void Place(int colour)
	{
		int slot = System.Array.FindIndex(_current, 0, CurrentLevel.Pegs, p => p == null);
		if (slot < 0)
			return;
		_hud.HideHint();
		var at = new Vector3(PegX(slot), PegRadius * 0.9f, RowZ(_game.Guesses.Count));
		var peg = MakePeg(colour, _rig, at + new Vector3(0, 1f, 0));
		peg.SetMeta("colour", colour);
		_current[slot] = peg;
		var drop = CreateTween();
		drop.TweenProperty(peg, "position", at, 0.18f).SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.In);
		drop.TweenCallback(Callable.From(() => Click(0.7f, peg.GlobalPosition)));
		RefreshUi();
	}

	private int[]? CurrentGuess()
	{
		var guess = new int[CurrentLevel.Pegs];
		for (int i = 0; i < guess.Length; i++)
		{
			if (_current[i] == null)
				return null;
			guess[i] = _current[i]!.GetMeta("colour").AsInt32();
		}
		return guess;
	}

	// ---------------------------------------------------------------- Turns

	private async Task Check()
	{
		var guess = CurrentGuess();
		if (_busy || guess == null)
			return;
		if (!_game.IsValid(guess))
		{
			_hud.SetStatus("No repeated colours at this level", Colors.White);
			return;
		}
		_busy = true;
		_spin.Stop();
		_check.Visible = false;
		int row = _game.Guesses.Count;
		var score = _game.Guess(guess);
		DevLog.Write($"guess {string.Join(",", guess)}: {score.Exact} exact, {score.Near} near");
		foreach (var peg in _current)
		{
			if (peg != null)
				_boardNodes.Add(peg);
		}
		System.Array.Clear(_current);

		// The key pegs pop in one by one, each a little higher in pitch.
		int k = 0;
		foreach (bool exact in Enumerable.Repeat(true, score.Exact).Concat(Enumerable.Repeat(false, score.Near)))
		{
			var key = MakeKey(row, k, exact);
			_boardNodes.Add(key);
			key.Scale = Vector3.One * 0.1f;
			CreateTween().TweenProperty(key, "scale", Vector3.One, 0.16f).SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
			_sound.Play(_impacts.Random(Style.Metal), key.GlobalPosition, Styles.GainDb(Style.Metal) + (exact ? 2f : -1f), 1f + k * 0.08f);
			k++;
			await Wait(0.16f);
		}

		if (_game.IsOver)
			await Reveal(_game.IsSolved);
		_busy = false;
		UpdateRowGlow();
		RefreshUi();
	}

	/// <summary>
	/// The signature: the cover over the secret lifts, flips away and falls aside, and the code
	/// glows in a burst of sparks (or simply shows, if you ran out of guesses).
	/// </summary>
	private async Task Reveal(bool solved)
	{
		_played++;
		if (solved)
			_solved++;
		var lift = CreateTween();
		lift.TweenProperty(_cover, "position:y", 1.6f, 0.3f).SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.Out);
		lift.Parallel().TweenProperty(_cover, "rotation:x", -Mathf.Pi * 0.9f, 0.6f);
		lift.TweenProperty(_cover, "position", _cover.Position + new Vector3(BoardWidth / 2 + 1.2f, -0.2f, 0.4f), 0.4f).SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.In);
		await Wait(0.35f);
		foreach (var peg in _secretPegs)
		{
			peg.Position = peg.Position with { Y = PegRadius * 0.9f };
			if (solved)
				peg.MaterialOverlay = Sparkle.Halo;
		}
		if (solved)
		{
			Sparkle.Burst(_rig, new Vector3(PegX(0) + (CurrentLevel.Pegs - 1) * Pitch / 2, 0.5f, SecretZ));
			for (int i = 0; i < _secretPegs.Count; i++)
				_sound.Play(_impacts.Random(Style.Glass), _secretPegs[i].GlobalPosition, 1f, 1f + i * 0.12f);
		}
		await ToSignal(lift, Tween.SignalName.Finished);
		Click(0.8f, _cover.GlobalPosition, onTable: true);
		if (solved)
		{
			// The cracked code catches fire, peg by peg.
			for (int i = 0; i < _secretPegs.Count; i++)
			{
				var peg = _secretPegs[i];
				_fires.Add(Flames.SetAlight(_rig, peg.Position, new[] { peg }, radius: 0.24f, size: 0.8f, withLight: i == 0));
				await Wait(0.12f);
			}
		}
	}

	private void Click(float strength, Vector3? at = null, bool onTable = false)
	{
		var position = at ?? _camera.GlobalPosition;
		_sound.PlayImpact(_impacts.Random(_settings.Pieces), position, strength, Styles.GainDb(_settings.Pieces));
		var surface = onTable ? Style.Table : _settings.Board;
		_sound.PlayImpact(_impacts.Random(surface), position, strength * 0.6f, Styles.GainDb(surface));
	}

	public override void _Process(double delta)
	{
		float dt = (float)delta;
		_spin.Process(dt);
		if (!_demo || _busy || _game.IsOver)
			return;
		// Demo: guess randomly (as a quick test of the whole flow).
		_demoTimer += dt;
		if (_demoTimer < 0.25f)
			return;
		_demoTimer = 0f;
		if (CurrentGuess() == null)
		{
			int slot = System.Array.FindIndex(_current, 0, CurrentLevel.Pegs, p => p == null);
			var used = _current.Where(p => p != null).Select(p => p!.GetMeta("colour").AsInt32()).ToHashSet();
			var choices = Enumerable.Range(0, CurrentLevel.Colours).Where(c => CurrentLevel.Repeats || !used.Contains(c)).ToList();
			// Late in the game, cheat so the demo also shows the reveal.
			int colour = _game.Guesses.Count >= 6 ? _game.Secret[slot] : choices[(int)(GD.Randi() % choices.Count)];
			Place(colour);
		}
		else
		{
			_ = Check();
		}
	}

	// ---------------------------------------------------------------- Games, hints, ads

	private void OnNewGamePressed()
	{
		if (_busy)
			return;
		bool adDue = _adSchedule!.GameFinished();
		_settings.GamesSinceAd = _adSchedule.GamesSinceAd;
		_settings.Save();
		if (adDue)
		{
			DevLog.Write("ad: between-games interstitial");
			_ads.ShowInterstitial(StartGame);
		}
		else
		{
			StartGame();
		}
	}

	private void StartGame()
	{
		PutOutFire();
		_game = new CodeGame(CurrentLevel);
		foreach (var peg in _current)
			peg?.QueueFree();
		BuildBoard();
		RefreshUi();
	}

	/// <summary>Starting over gives up on this code: it counts as unsolved unless you watch an ad.</summary>
	private void OnRestartPressed()
	{
		if (_busy)
			return;
		if (_game.IsOver || _game.Guesses.Count == 0)
		{
			StartGame();
			return;
		}
		_hud.AskRestart(true, countLoss =>
		{
			if (countLoss)
				_played++;
			StartGame();
		}, then => _ads.ShowRewarded(rewarded: then, closed: () => { }));
	}

	private readonly List<Flames> _fires = new();

	private void PutOutFire()
	{
		foreach (var fire in _fires)
			fire.Extinguish();
		_fires.Clear();
		foreach (var peg in _secretPegs)
			peg.MaterialOverlay = null;
	}

	// ---------------------------------------------------------------- Tutorial clips

	/// <summary>
	/// Plays one scripted scene for the tutorial (with --clip=name; Record-Clips.ps1 records it),
	/// then quits. The HUD is hidden so the clip shows only the board.
	/// </summary>
	private async Task PlayClip(string clip)
	{
		// The clip is square: frame the board more tightly than the tall phone screen does.
		_camera.Fov = 42;
		_camera.LookAt(new Vector3(0, 0f, 1.2f));
		_settings.Muted = false;
		_settings.Difficulty = clip == "blank" ? Difficulty.Hard : Difficulty.Medium;
		int blank = CurrentLevel.BlankColour;
		var (secret, before, shown) = clip switch
		{
			// Guess, read the key pegs, guess again.
			"guess" => (new[] { 1, 3, 0, 4 }, new int[][] { }, new[] { new[] { 0, 1, 2, 3 }, new[] { 1, 0, 3, 5 } }),
			// Late in a game: the last clue gives it away.
			"crack" => (new[] { 4, 2, 2, 0 }, new[] { new[] { 0, 1, 2, 3 }, new[] { 2, 2, 4, 0 }, new[] { 4, 0, 2, 2 } }, new[] { new[] { 4, 2, 2, 0 } }),
			// Hard: the blank counts as a colour.
			_ => (new[] { 2, blank, 5, blank }, new[] { new[] { 0, 1, 2, 3 } }, new[] { new[] { 2, blank, 6, 4 }, new[] { 2, blank, 5, blank } }),
		};
		_game = new CodeGame(CurrentLevel, secret);
		foreach (var guess in before)
			_game.Guess(guess);
		BuildBoard();
		RefreshUi();
		await Wait(0.8f);
		foreach (var guess in shown)
		{
			foreach (int colour in guess)
			{
				Place(colour);
				await Wait(0.42f);
			}
			await Wait(0.4f);
			await Check();
			await Wait(1.4f);
		}
		await Wait(clip == "crack" ? 1.8f : 0.6f);
		GetTree().Quit();
	}

	private bool CanHint => !_busy && !_game.IsOver && _game.Revealed.Count < CurrentLevel.Pegs - 1;

	private void OnHintPressed()
	{
		if (!CanHint)
			return;
		DevLog.Write("ad: rewarded (hint)");
		_hud.SetUndoVisible(false);
		_ads.ShowRewarded(rewarded: () =>
		{
			int peg = _game.RevealHint();
			if (peg >= 0)
			{
				ShowHints();
				Sparkle.Burst(_rig, _secretPegs[peg].Position + new Vector3(0, 0.3f, 0));
				_sound.Play(_impacts.Random(Style.Glass), _secretPegs[peg].GlobalPosition, 1f, 1.2f);
			}
		}, closed: RefreshUi);
	}

	// ---------------------------------------------------------------- UI

	private void RefreshUi()
	{
		_hud.SetScore($"Solved {_solved} of {_played}   ·   {ColourCount()}");
		_hud.SetNewGameVisible(_game.IsOver && !_busy);
		_hud.SetUndoVisible(CanHint);
		_hud.SetRestartVisible(!_game.IsOver);
		_check.Visible = !_game.IsOver && !_busy && CurrentGuess() != null;
		if (_game.IsOver)
		{
			if (_busy)
				return;
			if (_game.IsSolved)
				_hud.SetStatus($"Cracked in {_game.Guesses.Count}!", new Color("f5c542"));
			else
				_hud.SetStatus("Out of guesses: the code is shown", Colors.White);
			if (_demo && _quitAfterDemo)
				GetTree().CreateTimer(1.5).Timeout += () => GetTree().Quit();
			return;
		}
		_hud.SetStatus($"Guess {_game.Guesses.Count + 1} of {CurrentLevel.Guesses}", new Color("fff3e0"));
	}

	private string ColourCount()
	{
		var level = CurrentLevel;
		return level.Blank ? $"{level.Colours - 1} colours + blank" : $"{level.Colours} colours{(level.Repeats ? ", repeats" : ", no repeats")}";
	}

	private SignalAwaiter Wait(float seconds) => ToSignal(GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);

	// ---------------------------------------------------------------- Developer flags

	/// <summary>
	/// Developer flags after `--` (or in user://dev_args.txt on a phone): `--log`, `--demo` (guesses
	/// on its own), `--quit-after-demo`, `--games-since-ad=4`, `--board=Wood`, `--pieces=Glass`,
	/// `--difficulty=Hard`, `--spin=30`. Style flags aren't saved.
	/// </summary>
	private void ReadDeveloperArgs()
	{
		foreach (var (name, value) in DevArgs.Read())
		{
			switch (name)
			{
				case "--log":
					DevLog.Enabled = true;
					break;
				case "--demo":
					_demo = true;
					_settings.Muted = false;
					break;
				case "--quit-after-demo":
					_quitAfterDemo = true;
					break;
				case "--games-since-ad":
					_adSchedule = new AdSchedule(int.Parse(value));
					break;
				case "--board" when System.Enum.TryParse(value, out Style board):
					_settings.Board = board;
					break;
				case "--pieces" when System.Enum.TryParse(value, out Style pieces):
					_settings.Pieces = pieces;
					break;
				case "--difficulty" when System.Enum.TryParse(value, out Difficulty difficulty):
					_settings.Difficulty = difficulty;
					break;
				case "--spin":
					_initialSpin = Mathf.DegToRad(float.Parse(value, System.Globalization.CultureInfo.InvariantCulture));
					break;
				case "--clip":
					_clip = value;
					break;
			}
		}
	}
}
