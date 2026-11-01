local excluded_names = { ".mise", ".rumdl_cache", "artifacts", "bin", "node_modules", "obj", "TestResults" }

local excluded, tree_filters = {}, {}
for _, name in ipairs(excluded_names) do
  excluded[name] = true
  tree_filters[#tree_filters + 1] = "^" .. (name:gsub("%.", "\\.")) .. "$"
end

return {
  {
    "nvim-tree/nvim-tree.lua",
    optional = true,
    opts = { filters = { custom = tree_filters } },
  },
  {
    "stevearc/oil.nvim",
    optional = true,
    opts = {
      view_options = {
        is_always_hidden = function(name)
          return excluded[name] == true
        end,
      },
    },
  },
  {
    "stevearc/conform.nvim",
    optional = true,
    opts = { formatters_by_ft = { markdown = { "rumdl" }, mdx = { "rumdl" } } },
  },
  {
    "neovim/nvim-lspconfig",
    optional = true,
    init = function()
      vim.lsp.enable "rumdl"
    end,
  },
}
