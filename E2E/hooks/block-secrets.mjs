// A command hook for the e2e suite. Reads the call as JSON on stdin; refuses (exit code 2, reason on stderr) when the content mentions SECRET,
// otherwise answers with a note.
let input = '';
for await (let chunk of process.stdin) {
    input += chunk;
}
let call = JSON.parse(input);
let content = String(call.arguments?.content ?? '');
if (content.includes('SECRET')) {
    process.stderr.write('Secrets are not allowed in files.');
    process.exit(2);
}
process.stdout.write(JSON.stringify({ note: `scanned by script (${call.phase})` }));
