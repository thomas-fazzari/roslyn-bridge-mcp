local excluded = { artifacts = true, bin = true, node_modules = true, obj = true, TestResults = true }

return {
  {
    "nvim-tree/nvim-tree.lua",
    optional = true,
    opts = { filters = { custom = { "^artifacts$", "^bin$", "^node_modules$", "^obj$", "^TestResults$" } } },
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
}
