---
# Owner roster. The Sleeper username is the stable join key — display names and
# team names rotate, usernames don't. Usernames are NEVER rendered in output;
# the envelope builder maps username -> user_id and every downstream surface
# prints the `name` field instead.
#
# PRIVACY: first names only. No surnames, no last initials, no relationships.
owners:
  - username: robfoulk
    name: Rob
    aka: ["Rob"]
    notes: >
      Founding owner. Built the best regular-season team of 2024 (13-4) and
      still only finished third; followed it with a 7-10 slide in 2025 that he
      salvaged by winning the 7th-place game. Star-heavy, veteran-leaning
      rosters. Recurring team name "Unstoppable Farce" — self-aware elder voice.
  - username: Evenkeel75
    name: Brian
    aka: ["Brian", "Bri"]
    notes: >
      Founding owner. The league's most active trader. Went 5-12 in 2024, then
      posted the best record in league history at 14-3 in 2025 and lost in the
      semifinal anyway — the definitive "regular season means nothing" cautionary
      tale. Deep, established rosters.
  - username: asmartaleck1
    name: Dave
    aka: ["Dave", "David"]
    notes: >
      Founding owner. Bottomed out at 4-13 in 2024, then led the entire league in
      points for in 2025 (3192.30) and still lost the 3rd-place game. Team names
      run to grievance and pre-emptive doom ("He hate me", "Early and out of
      hope"). Elite anchors, thin middle.
  - username: ebmookie
    name: Eric
    aka: ["Eric"]
    notes: >
      Founding owner. Reached the 2024 championship game and lost it; won the
      2025 consolation final. Builds around specific NFL talent and stacks —
      the team name is usually a player pun.
  - username: jfoulkrod
    name: Jake
    aka: ["Jake", "Jacob"]
    notes: >
      Founding owner and the only two-time champion. Won 2024 from a 12-5 season
      and won 2025 as a 9-8 team that got hot at exactly the right time. Drafts
      young and volatile. Self-deprecating team names ("Seasonal Depression",
      "Performance Anxiety") — lean into them on losses, resist them on wins.
  - username: mafoulk
    name: Michael
    aka: ["Michael", "Mike", "Mikey"]
    notes: >
      Founding owner. Owns the worst season in league history (1-16 in 2024) and
      the best turnaround, reaching the 2025 championship game at 9-8. Balanced
      veteran cores. Team names stay bleak regardless of results.
  - username: Dbfoulkrod
    name: Devin
    aka: ["Devin"]
    notes: >
      Founding owner. Finished fourth in 2024, slid to 7-10 in 2025. Confident
      drafter who collects receivers. Team names reference his own players.
  - username: Von937
    name: Travon
    aka: ["Travon", "Von"]
    notes: >
      Joined for 2026 by purchasing an existing franchise slot. He inherits that
      roster and nothing else — not the previous owner's record, persona, team
      identity, or rivalries. He enters with no keepers.
  - username: NOTDoda
    name: Dakota
    aka: ["Dakota"]
    notes: >
      Former owner, 2024-2025. Won the 2024 consolation final, then finished last
      in 2025 and sold the franchise before 2026. His results stay attached to
      him as a former owner; they do not transfer to the new owner of that slot.
---

## Using owners

Refer to every owner by first name only. Usernames are internal keys and must
never appear in written output.

Owner history and franchise history are different things. A franchise slot can
change hands; a record does not. When a slot changes owners, results before the
sale belong to the previous owner and results after belong to the new one.
