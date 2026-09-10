-- ~/.config/nvim/init.lua - claude-roslyn-lsp as the C# language server (Neovim 0.11+, core API).
vim.lsp.config('claude_roslyn_lsp', {
  cmd = { '/usr/local/bin/claude-roslyn-lsp', 'lsp' },
  filetypes = { 'cs' },
  root_markers = { '.git' },
})

vim.lsp.enable('claude_roslyn_lsp')
