using System.Text.RegularExpressions;
using static Acta.Emit.Features.Learn.CSharpColour;

namespace Acta.Emit.Features.Learn;

/// <summary>The library's front page, <c>/learn</c>: the question, the doors, the look-alikes, and every section.</summary>
internal sealed partial class LearnPages
{
    private string RungRow(HowTo entry) =>
        $"<li><a class=\"rung\" href=\"{entry.Url}\" data-id=\"{entry.Id}\"><span class=\"rung-step\">{entry.Step}</span><span class=\"rung-text\"><span class=\"rung-title\">{Esc(entry.ShortTitle)}</span><span class=\"rung-note\">{Anchor().Replace(_text.Inline(entry.Summary, entry.Dir), "")}</span></span><span class=\"rung-marks\">{Marks(entry)}</span></a></li>";

    public string Hub()
    {
        var sections = string.Join(
            '\n',
            _library.Sections.Select(section =>
            {
                var rows = string.Join("\n          ", section.Entries.Select(RungRow));
                var more = section.MoreHref is null
                    ? ""
                    : $"\n        <a class=\"golink ladder-more\" href=\"{section.MoreHref}\">{Esc(section.MoreText!)}</a>";
                return $"""
                          <div class="ladder-section" id="s-{section.Key}" data-section="{section.Key}">
                            <div class="ladder-head">
                              <h3>{Esc(section.Title)}</h3>
                              <p>{Esc(section.Blurb)}</p>
                              <p class="ladder-progress" data-progress></p>
                            </div>
                            <ol class="rungs">
                              {rows}
                            </ol>{more}
                          </div>
                    """;
            })
        );

        // What the question on this page searches: the title, the words a person might type instead, the summary,
        // the section, and the titles of the steps.
        var index =
            "["
            + string.Join(
                ',',
                _library.Ordered.Select(e =>
                    $"{{\"u\":{Json(e.Url)},\"t\":{Json(e.ShortTitle)},\"s\":{Json(e.Section.Title)},\"k\":{Json($"{_library.SearchWords.GetValueOrDefault(e.Folder, "")} {e.Name}".Trim())},\"x\":{Json(string.Join(' ', e.Flow.Select(step => step.Title)))},\"d\":{Json(LearnText.Plain(e.Summary))},\"m\":{Json(e.Lab ? "Lab" : "")}}}"
                )
            )
            + "]";
        var tour = string.Concat(_library.Tour.Select(e => $"<li><a href=\"{e.Url}\" data-id=\"{e.Id}\">{Esc(e.ShortTitle)}</a></li>"));
        var lookAlikes = string.Join(
            "\n        ",
            _library.LookAlikes.Select(pair =>
            {
                var picks = string.Concat(
                    pair.Pick.Select(p =>
                        $"<li><span>{Esc(char.ToUpperInvariant(p.When[0]) + p.When[1..])}</span><a href=\"{p.Entry.Url}\" data-id=\"{p.Entry.Id}\">{Esc(p.Entry.ShortTitle)}</a></li>"
                    )
                );
                return $"<div><dt>{Esc(pair.Ask)}</dt><dd><ul>{picks}</ul></dd></div>";
            })
        );
        var jump = string.Concat(_library.Sections.Select(s => $"<a href=\"#s-{s.Key}\">{Esc(s.Title)}</a>"));
        var body = $"""
                <header class="wrap page-head ask">
                  <p class="record">learn acta</p>
                  <h1 class="ask-title"><label for="ask">How do I</label><span class="ask-field"><input id="ask" type="search" autocomplete="off" spellcheck="false" enterkeyhint="go" placeholder="wait for an approval" aria-controls="ask-results" /></span><span class="ask-tail">?</span></h1>
                  <p class="ask-try"><span>Try</span>
                    <button type="button" data-ask="run a job at 08:00 Berlin time">run a job at 08:00 Berlin time</button>
                    <button type="button" data-ask="keep a service in sync">keep a service in sync</button>
                    <button type="button" data-ask="hold a sign-up until it is confirmed">hold a sign-up until it is confirmed</button>
                    <button type="button" data-ask="wait for an approval">wait for an approval</button>
                    <button type="button" data-ask="stop retrying after three attempts">stop retrying after three attempts</button>
                  </p>
                  <div class="ask-results" id="ask-results" role="region" aria-live="polite" hidden></div>
                  <p class="lede">Every answer is a short how-to: the handler, the call that starts it, the command that runs it, and the lines it must print. The code is lifted from programs in the Acta repository, and each page says how its program is checked. Each page is also plain Markdown, so your coding agent learns from the page you read.</p>
                  {ReaderSwitch("/learn/index.md")}
                </header>

                <div class="person-face">
                <section class="wrap doors" aria-label="Where to start">
                  <a class="door" href="/learn/first-project">
                    <b>Build the first project</b>
                    <span>About an hour. A report service that retries, records its progress, and finishes the same job after you kill the process.</span>
                    <i>Start the tutorial</i>
                  </a>
                  <div class="door">
                    <b>Take the short tour</b>
                    <span>The how-tos that cover the model end to end, in order.</span>
                    <ol class="tour">{tour}</ol>
                  </div>
                  <a class="door" href="#agents">
                    <b>Hand it to your coding agent</b>
                    <span>Paste one prompt. The agent reads the same how-tos as Markdown and builds from them.</span>
                    <i>See the prompts</i>
                  </a>
                </section>

                <section class="wrap choices" id="which-one">
                  <p class="record">which one when</p>
                  <h2>When two how-tos look alike, this tells them apart.</h2>
                  <dl class="choice-list">
                    {lookAlikes}
                  </dl>
                </section>

                <section class="wrap ladder" id="how-tos">
                  <p class="record">the how-tos, section by section</p>
                  <h2>Each section is a ladder. Start at the top and stop when you have what you need.</h2>
                  <nav class="ladder-jump" aria-label="Sections">{jump}</nav>
            {sections}
                  <p class="hint ladder-ask">Looking for a how-to that is not here? <a href="{Repository}/issues/new">Tell us what you were trying to do</a>, and it becomes the next one.</p>
                </section>

                <section class="wrap" id="agents">
                  <p class="record">for coding agents</p>
                  <h2>Your coding agent reads the same how-tos.</h2>
                  <p class="lede">An agent does not need this page's design. It needs the smallest working example of the task, the rules that go with it, and a way to tell that it worked.</p>
                  <div class="facts">
                    <div><b>Every how-to is also Markdown</b><p>Add <code>.md</code> to a how-to's address and it answers as plain text: the same steps, code, rules and checks. Switch any page to "an agent" to see it.</p></div>
                    <div><b>One index lists them all</b><p><a href="/learn/index.md"><code>/learn/index.md</code></a> names every how-to by task, section by section, each with one line on what it teaches.</p></div>
                    <div><b>The output is the answer key</b><p>A how-to names the lines its program must print. The release check reads the same lines, so the page and the code cannot drift apart.</p></div>
                  </div>
                  <div class="prompts">
                    <figure class="listing"><figcaption><span class="note">Prompt: build from the how-tos</span><button class="copy" type="button">Copy</button></figcaption><pre><code>Read https://useacta.net/llms.txt, then https://useacta.net/learn/index.md. Pick the how-tos that match this task and read each one as Markdown: [describe the task]. Build it, run it once, and show me its job ref and its jobs explain output.</code></pre></figure>
                    <figure class="listing"><figcaption><span class="note">Prompt: teach me</span><button class="copy" type="button">Copy</button></figcaption><pre><code>Read https://useacta.net/learn/index.md and take me through the short tour, one how-to at a time. After each one that has a check question, ask me the question before you show the answer.</code></pre></figure>
                  </div>
                </section>

                <section class="wrap" id="more">
                  <p class="record">keep going</p>
                  <h2>Explainers you can play with, and the guides behind the how-tos.</h2>
                  <ul class="next-links wide">
                    <li><a href="/how-it-works">How Acta works</a><span>One job model for everything that outlives the request, in ten questions.</span></li>
                    <li><a href="/engine">Inside one worker</a><span>How one worker claims, runs, and finalizes a job.</span></li>
                    <li><a href="/fleet">Workers and recovery</a><span>Add as many workers as you like. They share the work through your database.</span></li>
                    <li><a href="/gates">Order and limits</a><span>Choose which jobs run in order, how many run at once, and how often they start.</span></li>
                    <li><a href="{Repository}/blob/main/docs/first-job.md">Your first job, end to end</a><span>Install Acta and run a durable job in your own app, then read, explain, cancel, and test it.</span></li>
                    <li><a href="{Repository}/blob/main/docs/developer-guide.md">Developer guide</a><span>How to understand, write, configure, and test jobs.</span></li>
                    <li><a href="{Repository}/blob/main/docs/scheduling.md">Scheduling and calendars</a><span>How a schedule becomes work, and what happens to the occurrences it missed.</span></li>
                    <li><a href="{Repository}/blob/main/docs/operator-guide.md">Operator guide</a><span>How to deploy, monitor, control, and troubleshoot Acta.</span></li>
                  </ul>
                </section>
                </div>

                <div class="wrap">{AgentFace("/learn/index.md")}</div>
                <script type="application/json" id="how-to-index">{index}</script>
            """;
        return Page(
            "Learn Acta · how-tos for durable workflows and jobs in .NET",
            "How to do one thing at a time with Acta: short how-tos with the handler, the call that starts it, the command that runs it, and the output it must print. Sectioned from the first job to production, and published as Markdown for coding agents.",
            "/learn",
            "/learn/index.md",
            body,
            [
                $"{{\"@context\":\"https://schema.org\",\"@type\":\"CollectionPage\",\"name\":\"Learn Acta\",\"url\":{Json(Origin + "/learn")},\"inLanguage\":\"en\",\"about\":{{\"@type\":\"SoftwareSourceCode\",\"name\":\"Acta\",\"programmingLanguage\":\"C#\",\"codeRepository\":{Json(Repository)}}}}}",
            ]
        );
    }

    [GeneratedRegex("<a [^>]*>|</a>")]
    private static partial Regex Anchor();
}
