import { docsSchema } from "@astrojs/starlight/schema";
import { defineCollection } from "astro:content";
import { glob } from "astro/loaders";

export const collections = {
  docs: defineCollection({
    loader: glob({ base: ".", pattern: ["**/[^_]*.mdx", "!{dist,node_modules}/**"] }),
    schema: docsSchema(),
  }),
};
