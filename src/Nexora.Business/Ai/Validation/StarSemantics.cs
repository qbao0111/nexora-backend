namespace Nexora.Business.Ai;

public static class StarSemantics
{
    public const string CanonicalInstructions = """
        STAR COMPONENT DEFINITIONS & EXTRACTION RULES:
        - SITUATION: Context, background, problem, or event in which the candidate acted.
          Examples: production incident, system degradation, conflict, deadline, failure, unexpected operational problem.
        - TASK: The candidate's personal responsibility, ownership, goal, or expected outcome.
          Examples: on-call responsibility, root-cause ownership, delivery target, deadline, assigned scope.
        - ACTION: Concrete steps actually performed by the candidate.
          Technical Action includes, but is NOT limited to:
          inspecting logs, querying metrics, running EXPLAIN ANALYZE, investigating pg_stat_statements, debugging, profiling, changing configuration, writing code, adding an index, implementing Redis/cache, writing migrations/scripts, deploying/rolling back, testing, mitigating an incident, making technical trade-offs, coordinating with Tech Lead, coordinating with DBA, coordinating with QA, communicating during an incident, documenting remediation.
          IMPORTANT: Technical verbs and implementation details MUST NOT be absorbed into Task just because the question asks about responsibility.
        - RESULT: Observed outcome caused by the actions.
          Examples: latency improved, error rate reduced, throughput increased, system recovered, incident resolved, release completed successfully, downtime avoided, data loss avoided, recovery happened within N minutes/hours, measurable metric improvement, RCA/runbook/documentation completed, operational/process improvement. A result does NOT have to be monetary.

        QUESTION FOCUS & SCANNING:
        - The focus of the interview question determines what should receive special attention, but it does NOT limit STAR extraction.
        - For EVERY behavioral answer: scan the ENTIRE current candidate answer for Situation, Task, Action, and Result, even when the question or follow-up focuses on only one component (e.g. if the question asks about responsibility, still detect Action and Result if present in the answer).
        - Do NOT mark Action or Result absent merely because the question primarily asked about Task.
        - Do not require artificial signpost words like 'Action:' or 'Result:'. Normal natural language technical answers must be detected directly.

        EVIDENCE-FIRST STAR EXTRACTION:
        For EACH component (situation, task, action, result):
        1. Search the candidate answer for direct evidence matching the component semantic.
        2. If direct evidence exists:
           - detected = true
           - evidence = "<concise direct quote from candidate answer>"
           - score = integer 1..100 according to specificity and quality:
             * 1-39: very weak/implicit evidence
             * 40-59: present but vague or incomplete
             * 60-79: clear and relevant evidence
             * 80-89: specific evidence with strong ownership/detail
             * 90-100: highly specific, concrete and measurable evidence
        3. If no qualifying evidence exists:
           - detected = false
           - evidence = ""
           - score = 0
        4. Write constructive feedback based on that result.
        INVARIANTS:
        - Never output detected=false with score > 0.
        - Never output detected=true with empty evidence.
        - Do not award score merely because the general rubric is high. STAR components are judged using their own evidence.
        """;
}
