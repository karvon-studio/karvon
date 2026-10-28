# The benchmark's image (bench/README.md): the toolchains, Claude Code's CLI, Karvon's runner and the tools Karvon would
# download, and the fixtures with their packages restored. Built by build-image.sh from its staging folder.
FROM mcr.microsoft.com/dotnet/sdk:10.0

ENV DEBIAN_FRONTEND=noninteractive \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    DISABLE_AUTOUPDATER=1 \
    CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC=1

RUN echo 'Acquire::ForceIPv4 "true";' > /etc/apt/apt.conf.d/99ipv4 \
    && apt-get update \
    && apt-get install -y --no-install-recommends python3 python3-pytest python3-venv sqlite3 git curl ca-certificates xz-utils \
       build-essential jq procps docker.io iptables util-linux \
    && (apt-get install -y --no-install-recommends docker-compose-v2 || apt-get install -y --no-install-recommends docker-compose) \
    && docker compose version \
    && rm -rf /var/lib/apt/lists/*

RUN curl -fsSL https://nodejs.org/dist/v24.21.0/node-v24.21.0-linux-x64.tar.xz | tar -xJ -C /usr/local --strip-components=1 \
    && curl -fsSL https://go.dev/dl/go1.26.1.linux-amd64.tar.gz | tar -xz -C /usr/local \
    && ln -s /usr/local/go/bin/go /usr/local/bin/go && ln -s /usr/local/go/bin/gofmt /usr/local/bin/gofmt

# The runs' user: the host's uid, so the workspaces and results it writes are the host user's; not root, which Claude
# Code's bypass permissions refuse.
RUN (userdel -r ubuntu 2>/dev/null || true) && useradd -m -u 1000 -s /bin/bash bench && usermod -aG docker bench
USER bench
RUN curl -fsSL https://sh.rustup.rs | sh -s -- -y --profile minimal --default-toolchain stable
USER root
ENV PATH=/home/bench/.cargo/bin:$PATH

COPY --chown=bench:bench staging/claude /usr/local/bin/claude
COPY --chown=bench:bench staging/karvon-tools/ /home/bench/.local/share/karvon/
COPY --chown=bench:bench staging/runner/ /opt/karvon-bench/
COPY --chown=bench:bench staging/images/ /opt/bench-images/
COPY --chown=bench:bench fixtures/ /bench/fixtures/
COPY --chown=bench:bench staging/karvon/ /bench/fixtures/karvon/
COPY --chown=bench:bench tasks/ /bench/tasks/
COPY entry.sh prepare-fixtures.sh verify-tasks.sh /usr/local/bin/
RUN chmod +x /usr/local/bin/entry.sh /usr/local/bin/prepare-fixtures.sh /usr/local/bin/verify-tasks.sh /usr/local/bin/claude

# The fixtures' packages, restored now: a run reaches only Anthropic's API.
USER bench
RUN git config --global user.name "Bench" && git config --global user.email "bench@localhost" && git config --global init.defaultBranch main \
    && prepare-fixtures.sh
USER root
# What Karvon's headless window draws with (Skia): fontconfig and a font.
RUN apt-get update && apt-get install -y --no-install-recommends libfontconfig1 fonts-dejavu-core && rm -rf /var/lib/apt/lists/*
ENTRYPOINT ["/usr/local/bin/entry.sh"]
