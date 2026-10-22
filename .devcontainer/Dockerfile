FROM mcr.microsoft.com/devcontainers/base:3-ubuntu26.04

# Native AOT links with clang and zlib. mise installs the rest of the tools
RUN install --directory --mode 755 /etc/apt/keyrings \
  && curl --fail --silent --show-error --location https://mise.jdx.dev/gpg-key.pub \
  | gpg --dearmor --output /etc/apt/keyrings/mise-archive-keyring.gpg \
  && echo "deb [signed-by=/etc/apt/keyrings/mise-archive-keyring.gpg] https://mise.jdx.dev/deb stable main" \
  > /etc/apt/sources.list.d/mise.list \
  && apt-get update \
  && apt-get install --yes --no-install-recommends clang zlib1g-dev mise \
  && rm -rf /var/lib/apt/lists/*
