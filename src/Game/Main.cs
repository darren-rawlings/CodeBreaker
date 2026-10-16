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
/// Code Breaker. Tap colours in the tray to fill the glowing row, then Check: gold pegs mean right
/// colour in the right place, silver right colour in the wrong place. Crack the code and the
/// cover over the secret lifts away in a burst of sparks (the signature "reveal"). A hint, paid for
/// with a rewarded ad, shows one peg of the code. Drag sideways to spin the board.
/// </summary>
public partial class Main : Node3D
{
	public const string Title = "Code Breaker";

	/// <summary>Google's test ad units until the real ones exist in AdMob (see the release playbook).</summary>
	private static readonly AdMobConfig AdUnits = AdMobConfig.Test;

	private const float Pitch = 0.82f;
	private const float RowPitch = 0.8f;
	private const float PegRadius = 0.29f;
	private const float KeyRadius = 0.1f;
	private const float Thickness = 0.4f;

	/// <summary>The peg colours, in order; levels use the first 6 or 8.</summary>
	private static readonly Color[] PegColours =
	{
		new("ef4444"), new("f97316"), new("facc15"), new("22c55e"), new("3b82f6"), new("a855f7"), new("f5f5f4"), new("ec4899"),
	};

	private static readonly string[] ColourNames = { "red", "orange", "yellow", "green", "blue", "purple", "white", "pink" };

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
	private float _initialSpin;
	private float _demoTimer;

	private Level CurrentLevel => Level.For(_settings.Difficulty);

	private float BoardWidth => CurrentLevel.Pegs * Pitch + 1.5f;

	private float BoardLength => CurrentLevel.Guesses * RowPitch + 1.9f;

	public override void _Ready()
	{
		DevLog.Tag = "cbr";
		AudioWatch.Attach(this, Title);
		_settings = GameSettings.Load(board: Style.Wood, pieces: Style.Glass);
		ReadDeveloperArgs();
		AudioServer.SetBusMute(0, _settings.Muted);

		_camera = new Camera3D { Position = new Vector3(0, 15.2f, 8.4f), Fov = 52 };
		AddChild(_camera);
		_camera.LookAt(new Vector3(0, 0f, 1.7f));
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
			UndoText: "Hint (ad)", LevelOnly: true),
			_settings.Muted, GameMode.PlayerVsComputer, _settings.Difficulty, _settings.Board, _settings.Pieces);
		_hud.NewGamePressed += OnNewGamePressed;
		_hud.UndoPressed += OnHintPressed;
		_hud.MuteToggled += muted =>
		{
			_settings.Muted = muted;
			AudioServer.SetBusMute(0, muted);
			_settings.Save();
		};
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
		var layer = new CanvasLayer();
		AddChild(layer);
		_check = new Button { Text = "Check", FocusMode = Control.FocusModeEnum.None, Visible = false };
		_check.AddThemeFontSizeOverride("font_size", 30);
		foreach (var state in new[] { "normal", "hover", "pressed", "hover_pressed" })
		{
			var box = new StyleBoxFlat { BgColor = state.StartsWith("pressed") ? theme.Accent.Darkened(0.2f) : theme.Accent, AntiAliasing = true };
			box.SetCornerRadiusAll(32);
			box.SetContentMarginAll(12);
			_check.AddThemeStyleboxOverride(state, box);
		}
		_check.SetAnchorsPreset(Control.LayoutPreset.CenterBottom);
		_check.OffsetLeft = -150;
		_check.OffsetRight = 150;
		_check.OffsetTop = -192;
		_check.OffsetBottom = -128;
		_check.Pressed += () => _ = Check();
		layer.AddChild(_check);

		_spin = new SpinControl(_rig, () => !_busy);
		_spin.Tapped += OnTapped;
		StartGame();
	}

	// ---------------------------------------------------------------- Board

	/// <summary>Row 0 (your first guess) is nearest you; the secret sits at the far end.</summary>
	private float RowZ(int row) => BoardLength / 2 - 0.9f - row * RowPitch;

	private float PegX(int index) => (index - (CurrentLevel.Pegs - 1) / 2f) * Pitch - 0.45f;

	private float SecretZ => -BoardLength / 2 + 0.65f;

	private Look PegLook(int colour)
	{
		var c = PegColours[colour];
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
		var look = exact ? Styles.Pieces(Style.Metal).One with { A = new Color("f5c542"), B = new Color("b8860b") } : Styles.Pieces(Style.Metal).Two;
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
		GetTree().CreateTimer(0.6).Timeout += () =>
		{
			foreach (var peg in _secretPegs)
				peg.MaterialOverlay = null;
		};
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
		_game = new CodeGame(CurrentLevel);
		foreach (var peg in _current)
			peg?.QueueFree();
		BuildBoard();
		RefreshUi();
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
		return $"{level.Pegs} pegs, {level.Colours} colours{(level.Repeats ? ", repeats" : "")}";
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
			}
		}
	}
}
