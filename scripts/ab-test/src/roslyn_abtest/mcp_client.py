"""Shared newline-delimited JSON-RPC client over a `roz-mcp` stdio subprocess.

Lifted out of generate_references.py so the run path (runner.py's prompt-render
bridge), the verifiers (verification.py's `accessibility-is`), and the oracle
generator can all drive the server without importing a top-level script. Adds
`get_prompt` (a `prompts/get` round-trip) on top of the tool-call surface the
oracle generator already used, plus `render_prompt` — the one-shot helper the
render bridge calls to turn `<prompt>(args)` into the recipe text a human would
get by typing the slash command.
"""
from __future__ import annotations

import json
import os
import queue
import shutil
import subprocess
import sys
import threading
import time

# Baseline MCP protocol revision; every C# SDK build negotiates up from this.
PROTOCOL_VERSION = "2024-11-05"
# nopCommerce cold-loads into the MSBuildWorkspace on the first *tool* call, so a
# warm-workspace call (e.g. find_symbol) blocks until the solution is loaded.
# Generous per-call ceiling. prompts/get never touches the workspace, so a render
# returns near-instantly even cold.
CALL_TIMEOUT_S = 600
INIT_TIMEOUT_S = 180


def resolve_roslyn_exe() -> str:
    """Locate roz-mcp on PATH, falling back to the dotnet global-tools dir."""
    found = shutil.which("roz-mcp")
    if found:
        return found
    fallback = os.path.join(
        os.path.expanduser("~"), ".dotnet", "tools", "roz-mcp.exe"
    )
    if os.path.isfile(fallback):
        return fallback
    sys.exit("roz-mcp not found on PATH or in ~/.dotnet/tools. Install the tool first.")


class McpStdioClient:
    """Minimal newline-delimited JSON-RPC client over an MCP stdio subprocess."""

    def __init__(self, command: str, env: dict[str, str]) -> None:
        self._proc = subprocess.Popen(
            [command],
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL,  # roz-mcp logs to a file sink, not stderr
            env=env,
            text=True,
            encoding="utf-8",
            bufsize=1,
        )
        self._inbox: queue.Queue[dict] = queue.Queue()
        self._next_id = 1
        self._reader = threading.Thread(target=self._read_loop, daemon=True)
        self._reader.start()

    def _read_loop(self) -> None:
        assert self._proc.stdout is not None
        for line in self._proc.stdout:
            line = line.strip()
            if not line:
                continue
            try:
                self._inbox.put(json.loads(line))
            except json.JSONDecodeError:
                # A clean MCP server emits only protocol JSON on stdout; tolerate
                # the occasional stray line rather than aborting the whole run.
                continue

    def _send(self, message: dict) -> None:
        assert self._proc.stdin is not None
        self._proc.stdin.write(json.dumps(message) + "\n")
        self._proc.stdin.flush()

    def _await(self, want_id: int, timeout: float) -> dict:
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            try:
                message = self._inbox.get(timeout=1.0)
            except queue.Empty:
                if self._proc.poll() is not None:
                    raise RuntimeError(
                        f"server exited (code {self._proc.returncode}) before "
                        f"responding to id={want_id}"
                    ) from None
                continue
            if message.get("id") == want_id:
                return message
            # Notifications / unrelated ids: ignore and keep reading.
        raise TimeoutError(f"no response to id={want_id} within {timeout:.0f}s")

    def initialize(self) -> None:
        """Run the MCP handshake: initialize request then initialized notification."""
        self._send(
            {
                "jsonrpc": "2.0",
                "id": self._next_id,
                "method": "initialize",
                "params": {
                    "protocolVersion": PROTOCOL_VERSION,
                    "capabilities": {},
                    "clientInfo": {"name": "abtest-client", "version": "1.0"},
                },
            }
        )
        response = self._await(self._next_id, INIT_TIMEOUT_S)
        self._next_id += 1
        if "error" in response:
            raise RuntimeError(f"initialize failed: {response['error']}")
        self._send({"jsonrpc": "2.0", "method": "notifications/initialized"})

    def request(self, method: str, params: dict, timeout: float) -> dict:
        """Send one JSON-RPC request and return its `result` payload (raises on error)."""
        call_id = self._next_id
        self._next_id += 1
        self._send({"jsonrpc": "2.0", "id": call_id, "method": method, "params": params})
        response = self._await(call_id, timeout)
        if "error" in response:
            raise RuntimeError(f"{method} failed: {response['error']}")
        return response.get("result", {})

    def list_tool_names(self, timeout: float = 60.0) -> list[str]:
        """Return the names of every tool the server advertises under its current env."""
        result = self.request("tools/list", {}, timeout)
        return [t["name"] for t in result.get("tools", [])]

    def call_tool(self, name: str, arguments: dict, timeout: float) -> str:
        """Call one tool and return its concatenated text content."""
        result = self.request("tools/call", {"name": name, "arguments": arguments}, timeout)
        blocks = result.get("content", [])
        text = "\n".join(b.get("text", "") for b in blocks if b.get("type") == "text")
        if result.get("isError"):
            raise RuntimeError(f"tools/call {name} returned isError=true: {text[:500]}")
        return text

    def get_prompt(self, name: str, arguments: dict, timeout: float) -> str:
        """Render a server prompt via `prompts/get`; return its messages' concatenated text.

        This is the render bridge's primitive: it exercises the real C# argument
        binding and returns the exact recipe message a slash-command invocation
        produces — no frozen copy to drift out of sync."""
        result = self.request("prompts/get", {"name": name, "arguments": arguments}, timeout)
        return "\n".join(
            m["content"]["text"]
            for m in result.get("messages", [])
            if isinstance(m.get("content"), dict) and m["content"].get("type") == "text"
        )

    def close(self) -> None:
        """Close stdin (signals the server to shut down) and reap the process."""
        try:
            if self._proc.stdin is not None:
                self._proc.stdin.close()
            self._proc.wait(timeout=15)
        except (OSError, subprocess.TimeoutExpired):
            self._proc.kill()


def render_prompt(
    prompt_name: str,
    arguments: dict[str, str],
    solution_path: str,
    *,
    exe: str | None = None,
) -> str:
    """Spin up a short-lived server pointed at `solution_path`, render one prompt, tear down.

    Faithful (real `prompts/get` round-trip exercising argument binding) and
    low-maintenance (no frozen recipe text). The render never touches the
    workspace, so it returns even before the eager solution load warms.
    """
    exe = exe or resolve_roslyn_exe()
    env = {
        **os.environ,
        "ROZ_SOLUTION_PATH": str(solution_path),
        # Prompts register unconditionally (WithPromptsFromAssembly is not gated by
        # ROZ_TOOLS), but ask for `all` anyway so the server's surface matches a
        # real fully-equipped session.
        "ROZ_TOOLS": "all",
        # Pin the log level: an ambient ROZ_LOG_LEVEL=Information leaks through
        # os.environ and balloons the per-file reload log.
        "ROZ_LOG_LEVEL": "Warning",
    }
    client = McpStdioClient(exe, env)
    try:
        client.initialize()
        return client.get_prompt(prompt_name, arguments, CALL_TIMEOUT_S)
    finally:
        client.close()
