# Code Breaker: design

Store title: **Code Breaker** (the generic name; "Mastermind" is a trademark). Package `com.renegadegames.codebreaker`.

## The game
The computer hides a code of coloured pegs under a cover. You have 12 guesses; each is scored with
red pegs (right colour, right place) and white pegs (right colour, wrong place), explained by a
legend on the board in front of the first row. Crack the code to win.

Levels (the Level button): always 4 pegs. Easy: 4 colours, no repeats. Medium: 6 colours with
repeats. Hard: 7 colours plus the blank (a faint, empty-looking peg that counts as a colour), with
repeats.

## Feel
- A wooden pegboard (any board material); marble-sized pegs whose finish follows the Style menu
  (plastic, wood, metal, glass, marble), always in distinct colours.
- Tap a colour in the tray to drop it into the next hole of the glowing row; tap a placed peg to
  take it out; Check appears when the row is full. Key pegs pop in one by one, rising in pitch.
- **Signature effect, the reveal:** cracking the code lifts the cover, flips it away onto the
  table, and lights the code up in a burst of sparks and a rising run of glass notes; then the code catches fire.
- Drag sideways to spin the board.

## Ads
Between-games interstitial every 5th game. **Hint (ad)**: a rewarded ad lifts one peg of the code
out of the cover (up to all but one).

## Later
Two-player mode (one sets the code, passing the phone); a daily code; statistics.
