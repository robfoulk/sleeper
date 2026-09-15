import league from '../data/league.json';

export type Standing = {
  franchise_id: number;
  owner_name: string;
  team_name: string;
  final_place: number;
  wins: number;
  losses: number;
  ties: number;
  points_for: number;
  points_against: number;
  total_wins: number;
  total_losses: number;
  high_score: number;
  high_score_week: number;
  low_score: number;
  low_score_week: number;
  longest_win_streak: number;
  longest_loss_streak: number;
  next_year_draft_pick: number;
};

export type Side = { franchise_id: number; owner_name: string; team_name: string; points: number };
export type Game = { home: Side; away: Side; margin: number };
export type Week = { week: number; is_playoff: boolean; games: Game[] };

export type Season = {
  season: number;
  league_id: string;
  league_name: string;
  schedule: {
    regular_season_last_week: number;
    playoff_start_week: number;
    championship_week: number;
    total_weeks: number;
  };
  champion?: { owner_name: string; team_name: string } | null;
  standings: Standing[];
  weeks: Week[];
  weekly_champions: { week: number; owner_name: string; team_name: string; points_for: number }[];
  highlights: Record<string, any>;
  awards: {
    name: string;
    description: string;
    owner_name: string;
    team_name: string;
    metric: string;
    value: number;
    week?: number;
    citation?: string;
  }[];
};

export type Franchise = {
  franchise_id: number;
  current_team_name: string;
  current_owner_name: string;
  owner_history: string[];
  timeline: {
    season: number;
    owner_name: string;
    team_name: string;
    wins: number;
    losses: number;
    ties: number;
    points_for: number;
    points_against: number;
    final_place: number;
  }[];
  championships: number;
};

export type Owner = {
  name: string;
  active: boolean;
  first_season: number;
  last_season: number;
  franchises: number[];
  seasons: {
    season: number;
    team_name: string;
    wins: number;
    losses: number;
    ties: number;
    points_for: number;
    final_place: number;
  }[];
  career: {
    seasons_played: number;
    wins: number;
    losses: number;
    ties: number;
    points_for: number;
    points_against: number;
    championships: number;
    best_finish: number;
    worst_finish: number;
  };
};

export type Article = { season: number; week?: number; kind: string; path: string; title: string };

export const data = league as unknown as {
  generated_at_utc: string;
  league_name: string;
  seasons: Season[];
  franchises: Franchise[];
  owners: Owner[];
  records: {
    notes: string[];
    all_time: {
      owner_name: string;
      wins: number;
      losses: number;
      ties: number;
      points_for: number;
      points_against: number;
      championships: number;
    }[];
    championships: { season: number; owner_name: string; team_name: string; runner_up?: string }[];
    single_week_highs: { season: number; week: number; owner_name: string; team_name: string; points: number }[];
    single_week_lows: { season: number; week: number; owner_name: string; team_name: string; points: number }[];
    head_to_head: {
      owner_name: string;
      opponent_name: string;
      wins: number;
      losses: number;
      ties: number;
      points_for: number;
      points_against: number;
    }[];
  };
  articles: Article[];
  draft: {
    season: string;
    keeper_summary: { roster_id: number; owner_name: string; keeper_count: number; rounds_spent: number[] }[];
    verification: string[];
  } | null;
};

/** The upcoming season has a draft but no results, so it is listed separately from played seasons. */
export const PLAYED_SEASONS = data.seasons.map((s) => s.season).sort((a, b) => b - a);

export const ALL_SEASONS = Array.from(
  new Set([
    ...PLAYED_SEASONS,
    ...data.articles.map((a) => a.season),
    ...(data.draft ? [Number(data.draft.season)] : []),
  ]),
)
  .filter((n) => Number.isFinite(n))
  .sort((a, b) => b - a);

export const CURRENT_SEASON = ALL_SEASONS[0];

export const seasonOf = (year: number) => data.seasons.find((s) => s.season === year);

export const articlesOf = (year: number) =>
  data.articles
    .filter((a) => a.season === year)
    .slice()
    .sort((a, b) => (a.week ?? 99) - (b.week ?? 99));

/** `recaps/2025/week-05.md` is the collection id `2025/week-05`, and the route matches. */
export const articleSlug = (path: string) => path.replace(/^recaps\//, '').replace(/\.md$/, '');

/**
 * Generated titles carry the league and season, which the masthead and the section slug
 * already say. Stripping the suffix keeps headlines from reading like filenames.
 */
export const shortTitle = (title: string) =>
  title
    .replace(/\s*[—-]\s*The League\s*\(\d{4}\)\s*$/i, '')
    .replace(/^(\d{4})\s+The League\s*[—-]\s*/i, '$1 ')
    .trim();

export const ownerOf = (name: string) => data.owners.find((o) => o.name === name);

export const ownerSlug = (name: string) => name.toLowerCase().replace(/[^a-z0-9]+/g, '-');

export const record = (w: number, l: number, t: number) => (t ? `${w}-${l}-${t}` : `${w}-${l}`);

export const pts = (n: number | null | undefined) =>
  n === null || n === undefined ? '—' : n.toFixed(2);

export const pts1 = (n: number | null | undefined) =>
  n === null || n === undefined ? '—' : n.toFixed(1);

const ORDINALS = ['0th', '1st', '2nd', '3rd', '4th', '5th', '6th', '7th', '8th', '9th', '10th'];
export const ordinal = (n: number) => ORDINALS[n] ?? `${n}th`;

export const KIND_LABEL: Record<string, string> = {
  'weekly-recap': 'Weekly recap',
  'season-review': 'Season in review',
  'draft-recap': 'Draft recap',
  'season-preview': 'Season preview',
};
