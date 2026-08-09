---
# League lore — owners, family, and rivalries.
# The Sleeper username is the stable key. Display names and team names rotate;
# usernames don't. The builder maps username -> user_id at envelope build time.
league:
  name: "FoulknFootball"
  surname: "Foulkrod"
  notes: |
    Eight-team family league. Every owner is a Foulkrod. Generation 1 is the three
    brothers (Rob, Dave, Brian) plus their cousin Eric. Generation 2 is four sons.
    Roughly half of any week's matchups have a built-in family hook (brother vs
    brother, father vs son, sibling Gen-2, cousin vs nephew, generation war).
owners:
  # Generation 1
  - username: robfoulk
    name: Rob
    aka: ["Rob Foulkrod", "Rob"]
    generation: 1
    notes: >
      Brother. Last-season team name "Unstoppable Farce" — tongue-in-cheek elder
      voice. Father of Jake. Brother to Dave and Brian. Cousin to Eric.
  - username: Evenkeel75
    name: Brian
    aka: ["Brian Foulkrod", "Brian", "Bri"]
    generation: 1
    notes: >
      Brother. Last-season team name "First in and First Out?" — trade-aggressive,
      decisive-or-impulsive owner persona. Father of Devin. Brother to Rob and Dave.
      Cousin to Eric.
  - username: asmartaleck1
    name: Dave
    aka: ["Dave Foulkrod", "Dave", "David"]
    generation: 1
    notes: >
      Brother. Last-season team name "He hate me" — grievance / underdog persona.
      Father of Michael and Dakota. Brother to Rob and Brian. Cousin to Eric.
  - username: ebmookie
    name: Eric
    aka: ["Eric Foulkrod", "Eric"]
    generation: 1
    notes: >
      Cousin to Rob, Dave, and Brian. Last-season team name "Sanders Boutte on
      Sunday" — player-stack pun; builds around specific NFL talent.

  # Generation 2
  - username: jfoulkrod
    name: Jake
    aka: ["Jake Foulkrod", "Jake", "Jacob"]
    generation: 2
    notes: >
      Rob's son. Last-season team name "Seasonal Depression" — self-deprecating;
      lean into it on losses, but show some respect on wins.
  - username: mafoulk
    name: Michael
    aka: ["Michael Foulkrod", "Michael", "Mike", "Mikey"]
    generation: 2
    notes: >
      Dave's son. Last-season team name "Disappointment" — self-deprecating, plus
      latent father/son tension with Dave. Brother to Dakota.
  - username: Dbfoulkrod
    name: Devin
    aka: ["Devin Foulkrod", "Devin"]
    generation: 2
    notes: >
      Brian's son. Last-season team name "Amon Another level" (Amon-Ra St. Brown
      reference). Confident young owner.
  - username: NOTDoda
    name: Dakota
    aka: ["Dakota Foulkrod", "Dakota"]
    generation: 2
    notes: >
      Dave's son. Last-season team name "BearDown" — Bears fan; lean
      pessimistic-with-pride. Brother to Michael.

# Story-hook resolver. Order matters (first match wins when surfacing the hook).
relationships:
  - type: father_son
    pairs:
      - [robfoulk, jfoulkrod]
      - [asmartaleck1, mafoulk]
      - [asmartaleck1, NOTDoda]
      - [Evenkeel75, Dbfoulkrod]
    label: "Father vs Son"
  - type: brother
    pairs:
      - [robfoulk, Evenkeel75]
      - [robfoulk, asmartaleck1]
      - [Evenkeel75, asmartaleck1]
    label: "Brother Bowl"
  - type: gen2_siblings
    pairs:
      - [mafoulk, NOTDoda]
    label: "Brother Bowl (Gen 2)"
  - type: cousin
    pairs:
      - [ebmookie, robfoulk]
      - [ebmookie, Evenkeel75]
      - [ebmookie, asmartaleck1]
    label: "Cousin Bowl"
  - type: cousin_nephew
    pairs:
      - [ebmookie, jfoulkrod]
      - [ebmookie, mafoulk]
      - [ebmookie, NOTDoda]
      - [ebmookie, Dbfoulkrod]
    label: "Cousin vs Nephew"
  - type: gen_war
    # Any G1 vs G2 not already matched as father/son
    label: "Old Guard vs Young Guns"
---

## Relationships

The Foulkrod League is a family affair. Every owner is a Foulkrod. The dads —
**Rob**, **Dave**, and **Brian** — are brothers; **Eric** is their cousin. The
four Gen-2 owners are their sons: **Jake** (Rob's son), **Michael** and
**Dakota** (Dave's sons), and **Devin** (Brian's son).

Story hooks the recap should reach for, in priority order:

1. **Father vs Son.** When Rob faces Jake, or Dave faces Michael or Dakota, or
   Brian faces Devin, that's the headline matchup of the week. Dinner-table
   stakes. Name the dad and son explicitly; lean into the lineage but don't
   make it cruel.
2. **Brother Bowl.** Rob/Dave, Rob/Brian, or Dave/Brian — the elder Foulkrods
   settling something they've probably been settling since childhood.
3. **Brother Bowl (Gen 2).** Michael vs Dakota — Dave's two sons. Dad is
   watching.
4. **Cousin Bowl.** Eric facing any of Rob/Dave/Brian. They are all cousins to
   each other — the cousin (Eric) carrying his slice of the family name into
   the dads' bracket.
5. **Cousin vs Nephew.** Eric vs any Gen-2 owner. The "uncle Eric" energy.
6. **Old Guard vs Young Guns.** Any other G1 vs G2 game.

Last-season team names tell you something about each owner's mood; if a team
name *changes* mid-season, surface it as flavor (the rename almost always
betrays how the season has been going). Self-deprecating names ("Seasonal
Depression", "Disappointment") get leaned into on losses but resisted on wins.

The league-wide framing — "a Foulkrod family affair", "Foulkrod civil war" —
is available but should be used sparingly.
