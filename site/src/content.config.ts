import { defineCollection } from 'astro:content';
import { glob } from 'astro/loaders';

// The recaps are the source of truth and stay where the CLI writes them. The site reads them
// in place rather than keeping a second copy that could drift from the generated originals.
const articles = defineCollection({
  loader: glob({ pattern: '**/*.md', base: '../recaps' }),
});

export const collections = { articles };
