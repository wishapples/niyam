# Niyam

[![selftest](https://github.com/wishapples/niyam/actions/workflows/selftest.yml/badge.svg)](https://github.com/wishapples/niyam/actions/workflows/selftest.yml)

**Answers GST and company-law questions from your own library of official documents, cites the exact page, and checks
its own numbers.**

Built for chartered accountants, company secretaries and finance teams in India. You keep a folder of the official
documents you rely on (Acts, Rules, notifications, circulars, your own notes). Niyam searches them in English, Hindi or
Marathi, writes a short answer with numbered citations using NVIDIA AI models, and then checks the answer: every rate,
amount, date and section number must appear in the passage it cites. If one does not, you see a warning.

```
you › What is the late fee for filing the monthly return late?

The late fee is Rs. 50 for each day of delay [1], up to Rs. 5,000 per return [1].

Check passed: 1 of 1 sentence(s) cite a source, and every number in the answer appears in the passage it cites.

Sources:
  [1] Test Board Test Circular, 7/2099-XYZ, 01 Jan 2099, p. 1  https://…

This is not legal or tax advice. Check the cited source before acting.
```
*(Example from the fictional test documents.)*

## Why it is different

| | |
|---|---|
| **Your library, not ours** | Answers come only from documents you chose, so you know what it read. Nothing is answered from the model's memory. |
| **Checks its own numbers** | The commonest AI mistake in tax work is a wrong rate or amount. Niyam compares every number in the answer with the cited passage and warns on any mismatch. |
| **Says when it does not know** | If your library does not cover the question, it says so and shows the closest passages. |
| **Private** | Documents and the index stay on your computer. With an NVIDIA key, only the question and the few retrieved passages are sent to NVIDIA's API. Without a key, everything runs offline. |
| **Indian languages** | Hindi and Marathi questions and documents (Devanagari) work in keyword search; NVIDIA's multilingual retrieval models add cross-language search. |
| **Works inside AI apps** | An MCP server lets Claude Desktop, GitHub Copilot in VS Code and Cursor search your library. |

## How it uses NVIDIA

| Step | NVIDIA model (via [build.nvidia.com](https://build.nvidia.com)) |
|---|---|
| Find passages by meaning, across languages | NeMo Retriever `llama-3.2-nv-embedqa-1b-v2` (embeddings) |
| Put the best passages first | NeMo Retriever `llama-3.2-nv-rerankqa-1b-v2` (reranking) |
| Write the answer with citations | any NIM chat model (`NIM_MODEL`; default `meta/llama-3.3-70b-instruct`; Nemotron models work too) |

Search combines keyword ranking (BM25, always on) with the embeddings by reciprocal-rank fusion, then reranks. If any
NVIDIA call fails, Niyam falls back to the next simpler step and tells you.

NVIDIA's free API key is for development and testing. For a paid service, use NVIDIA AI Enterprise or run the same
models yourself (they are available as downloadable NIM containers).

## Quick start (Windows, macOS or Linux)

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```
git clone https://github.com/wishapples/niyam
cd niyam
dotnet build -c Release
```

1. **Build your library.** Download official documents into the `library` folder, for example:
   - the CGST Act and other Acts from India Code (indiacode.nic.in),
   - CGST Rules, notifications and circulars from CBIC (cbic-gst.gov.in),
   - Companies Act rules and notifications from MCA (mca.gov.in),
   - the startup recognition notification from DPIIT / Startup India.

   Describe each file in `library/sources.csv` so citations show the issuer, number, date and link:
   ```
   file,title,issuer,number,date,url,status
   cgst_act.pdf,Central Goods and Services Tax Act 2017,Parliament,Act 12 of 2017,12 Apr 2017,https://…,in force
   circular_xyz.pdf,Clarification on …,CBIC,Circular No. …,…,https://…,superseded
   ```
   Use `status` to mark documents that are amended or superseded; answers will say so. `niyam fetch` downloads every
   file listed with a URL that is not in the folder yet.
2. **Index it:** `niyam ingest` (run again whenever you add or change documents; unchanged files are skipped).
3. **Ask:** `niyam ask "What is the time limit to claim input tax credit?"` or `niyam chat`.

The program is in `src/Niyam.Cli/bin/Release/net10.0/` (`niyam.exe` on Windows).

**NVIDIA key (optional).** Get a free key at build.nvidia.com, then set it in your own terminal (never put it in a file
or on GitHub):
```
set NVIDIA_API_KEY=nvapi-...        (Windows)
export NVIDIA_API_KEY=nvapi-...     (macOS / Linux)
```
Run `niyam ingest` again to add embeddings. Without a key, `ask` shows the most relevant passages instead of writing an answer.

## Commands

| Command | |
|---|---|
| `ingest` | Index the library folder (PDF, HTML, TXT, MD) |
| `ask "question"` | Answer with citations and the number check |
| `search "question"` | Show the most relevant passages only |
| `chat` | Several questions in a row |
| `sources` | The documents, their source details and any reading problems |
| `fetch` | Download documents listed in `sources.csv` |
| `mcp` | MCP server for AI apps (below) |

Options: `--library <folder>` (default `./library`, or `NIYAM_LIBRARY`), `--k <passages>` (default 6).
Environment: `NVIDIA_API_KEY`, `NIM_MODEL`, `NIM_BASE_URL`, `NIM_EMBED_MODEL`, `NIM_RERANK_MODEL`, `NIM_RERANK_URL`.

## Use it from Claude Desktop, VS Code or Cursor

```json
{
  "mcpServers": {
    "niyam": {
      "command": "C:\\niyam\\src\\Niyam.Cli\\bin\\Release\\net10.0\\niyam.exe",
      "args": ["mcp", "--library", "C:\\niyam\\library"]
    }
  }
}
```
(`claude_desktop_config.json` for Claude Desktop, `~/.cursor/mcp.json` for Cursor; in VS Code use `.vscode/mcp.json`
with `"servers"` and `"type": "stdio"`.) Tools: `search_library`, `ask`, `list_sources`, `read_passage`, all read-only.

## Honest limits

- **Only as current as your library.** Niyam does not know about a notification you have not added. Keep the library
  up to date and mark superseded documents in `sources.csv`.
- **Scanned PDFs** have no text layer; Niyam reports them and they need OCR first.
- **Hindi PDFs** made with older non-Unicode fonts may extract as garbled text; Unicode PDFs, HTML and text files work.
- **The number check** catches wrong numbers, not wrong reasoning. A sentence can cite the right passage and still
  misread it. Always open the cited page before acting.
- **Not legal or tax advice.** It is a research tool for professionals.

## Tests

```
dotnet run --project tests/Niyam.SelfTest
```
35 checks on fictional test documents (clearly marked; no real legal text is bundled): PDF, HTML and Hindi text
reading, incremental indexing, keyword search in English and Hindi, the number check (wrong amounts, bad citations,
uncited numbers), the NVIDIA embedding, reranking and answer path against a local mock, fallbacks, and the MCP protocol.

## Licence

MIT, © 2026 Wish Apples Ventures Private Limited. PDF reading uses [PdfPig](https://github.com/UglyToad/PdfPig)
(Apache-2.0). NVIDIA models are subject to NVIDIA's terms.
