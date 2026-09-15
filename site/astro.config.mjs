// @ts-check
import { defineConfig } from 'astro/config';

/**
 * Recap markdown opens with its own `# Week N Recap — The League (YYYY)` heading, but the
 * article route already renders that title as the page's h1. Left alone every article ships
 * two top-level headings for one document, which misreports the outline to screen readers.
 * The route's heading is the better one — it is already shortened — so the markdown's leading
 * h1 is dropped here rather than in each template.
 */
function stripLeadingH1() {
  return (tree) => {
    const i = tree.children.findIndex(
      (n) => n.type === 'element' && /^h[1-6]$/.test(n.tagName)
    );
    if (i !== -1 && tree.children[i].tagName === 'h1') {
      tree.children.splice(i, 1);
    }
  };
}

// Published to GitHub Pages from this repository, so the site lives under /sleeper.
export default defineConfig({
  site: 'https://rob-foulkrod.github.io',
  base: '/sleeper',
  trailingSlash: 'ignore',
  build: { format: 'directory' },
  markdown: {
    rehypePlugins: [stripLeadingH1],
  },
});
