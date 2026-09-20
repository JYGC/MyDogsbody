# Sizes the comment blocks in F# / C# source, one tab-separated row per block:
#   kind, lineCount, wordCount, file:firstLine, whatTheBlockSitsOn, excerpt
#
# A HEURISTIC, for sizing a change and for its exit check. It cannot tell a description from
# a rationale by meaning, only by vocabulary, so every block a person acts on is still read.
#
# Kinds:
#   ARRANGE-ACT-ASSERT   a test-phase marker that a binding name can carry
#   BANNER               a section divider that a nested module can carry
#   RATIONALE            mentions why / history / evidence / a spec citation; a name cannot hold it
#   DESCRIBES-DECLARATION  no rationale vocabulary, and directly above a declaration:
#                          the candidates a longer name can absorb
#   DESCRIBES-STATEMENT    no rationale vocabulary, inside a body: an intermediate let can absorb it

function classifyPendingBlock(   wordCount, kind) {
    if (linesInBlock == 0) return

    wordCount = split(blockText, ignoredWords, /[ \t]+/)

    if (blockText ~ /^[ \t]*(Arrange|Act|Assert)/)
        kind = "ARRANGE-ACT-ASSERT"
    else if (dividerLinesInBlock == linesInBlock)
        kind = "BANNER"
    else if (blockText ~ /requirements\.md|design\.md|outcome\.md|PR #|used to|easured|review round|Phase [0-9]|Q[0-9]+\.[0-9]+/)
        kind = "RATIONALE"
    else if (blockText ~ /because|since |so that|so the |so it |so a |rather than|deliberately/)
        kind = "RATIONALE"
    else
        kind = "UNDECIDED"

    pendingKind = kind
    pendingLineCount = linesInBlock
    pendingWordCount = wordCount
    pendingFile = FILENAME
    pendingFirstLine = firstLineOfBlock
    pendingExcerpt = substr(blockText, 1, 100)
    blockIsWaitingForItsFollowingLine = 1

    linesInBlock = 0
    blockText = ""
    dividerLinesInBlock = 0
}

function reportPendingBlock(followingLine,   trimmedFollowingLine, sitsOnDeclaration, finalKind, sitsOn) {
    if (!blockIsWaitingForItsFollowingLine) return

    trimmedFollowingLine = followingLine
    sub(/^[ \t]*/, "", trimmedFollowingLine)
    sitsOnDeclaration = (trimmedFollowingLine ~ /^(let|type|member|and|module|val|static|public|private|internal|\[<)/ \
                         || trimmedFollowingLine ~ /^\|/ \
                         || trimmedFollowingLine ~ /^[A-Z][A-Za-z0-9]*[ \t]*:/)

    finalKind = pendingKind
    if (pendingKind == "UNDECIDED")
        finalKind = sitsOnDeclaration ? "DESCRIBES-DECLARATION" : "DESCRIBES-STATEMENT"

    sitsOn = sitsOnDeclaration ? substr(trimmedFollowingLine, 1, 70) : ""
    gsub(/\t/, " ", sitsOn)

    printf "%s\t%d\t%d\t%s:%d\t%s\t%s\n", finalKind, pendingLineCount, pendingWordCount, pendingFile, pendingFirstLine, sitsOn, pendingExcerpt
    blockIsWaitingForItsFollowingLine = 0
}

{ strippedLine = $0; sub(/^[ \t]+/, "", strippedLine); sub(/\r$/, "", strippedLine) }

strippedLine ~ /^\/\// {
    if (linesInBlock == 0) {
        reportPendingBlock("")
        firstLineOfBlock = FNR
    }
    bodyOfLine = strippedLine
    sub(/^\/\/\/?[ \t]?/, "", bodyOfLine)
    if (bodyOfLine ~ /^[-=*_]{4,}/) dividerLinesInBlock++
    blockText = blockText " " bodyOfLine
    linesInBlock++
    next
}

{
    if (linesInBlock > 0) classifyPendingBlock()
    reportPendingBlock($0)
}

FNR == 1 && FILENAME != previousFilename { previousFilename = FILENAME }
END { if (linesInBlock > 0) classifyPendingBlock(); reportPendingBlock("") }
