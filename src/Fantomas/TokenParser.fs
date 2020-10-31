module internal Fantomas.TokenParser

open System.Text.RegularExpressions
open FSharp.Compiler.AbstractIL.Internal.Library
open System
open System.Text
open FSharp.Compiler.SourceCodeServices
open Fantomas
open Fantomas.TokenParserBoolExpr
open Fantomas.TriviaTypes

let private whiteSpaceTag = 4
let private lineCommentTag = 8
let private greaterTag = 160

// workaround for cases where tokenizer dont output "delayed" part of operator after ">."
// See https://github.com/fsharp/FSharp.Compiler.Service/issues/874
let private isTokenAfterGreater token (greaterToken: Token) =
    let greaterToken = greaterToken.TokenInfo

    greaterToken.Tag = greaterTag
    && token.Tag <> greaterTag
    && greaterToken.RightColumn <> (token.LeftColumn + 1)

let private getTokenText (sourceCodeLines: string list) line (token: FSharpTokenInfo) =
    sourceCodeLines.[line - 1]
        .Substring(token.LeftColumn, token.RightColumn - token.LeftColumn + 1)
    |> String.normalizeNewLine

/// Tokenize a single line of F# code
let rec private tokenizeLine (tokenizer: FSharpLineTokenizer) sourceCodeLines state lineNumber tokens =
    match tokenizer.ScanToken(state), List.tryHead tokens with
    | (Some tok, state), Some greaterToken when (isTokenAfterGreater tok greaterToken) ->
        let extraTokenInfo =
            { tok with
                  TokenName = "DELAYED"
                  LeftColumn = greaterToken.TokenInfo.RightColumn + 1
                  Tag = -1
                  CharClass = FSharpTokenCharKind.Operator
                  RightColumn = tok.LeftColumn - 1 }

        let extraToken =
            { TokenInfo = extraTokenInfo
              LineNumber = lineNumber
              Content = getTokenText sourceCodeLines lineNumber extraTokenInfo }

        let token =
            { TokenInfo = tok
              LineNumber = lineNumber
              Content = getTokenText sourceCodeLines lineNumber tok }

        tokenizeLine tokenizer sourceCodeLines state lineNumber (token :: extraToken :: tokens)

    | (Some tok, state), _ ->
        let token: Token =
            { TokenInfo = tok
              LineNumber = lineNumber
              Content = getTokenText sourceCodeLines lineNumber tok }
        // Tokenize the rest, in the new state
        tokenizeLine tokenizer sourceCodeLines state lineNumber (token :: tokens)

    | (None, state), _ -> state, tokens

let private tokenizeLines (sourceTokenizer: FSharpSourceTokenizer) allLines state =
    allLines
    |> List.mapi (fun index line -> line, (index + 1)) // line number is needed in tokenizeLine
    |> List.fold (fun (state, tokens) (line, lineNumber) ->
        let tokenizer =
            sourceTokenizer.CreateLineTokenizer(line)

        let nextState, tokensOfLine =
            tokenizeLine tokenizer allLines state lineNumber []

        let allTokens =
            List.append tokens (List.rev tokensOfLine) // tokens of line are add in reversed order

        (nextState, allTokens)) (state, []) // empty tokens to start with
    |> snd // ignore the state

let private createHashToken lineNumber content fullMatchedLength offset =
    let (left, right) = offset, String.length content + offset

    { LineNumber = lineNumber
      Content = content
      TokenInfo =
          { TokenName = "HASH_IF"
            LeftColumn = left
            RightColumn = right
            ColorClass = FSharpTokenColorKind.PreprocessorKeyword
            CharClass = FSharpTokenCharKind.WhiteSpace
            FSharpTokenTriggerClass = FSharpTokenTriggerClass.None
            Tag = 0
            FullMatchedLength = fullMatchedLength } }

type SourceCodeState =
    | Normal
    | InsideString
    | InsideTripleQuoteString of int

let rec private getTokenizedHashes (sourceCode: string): Token list =
    if not (sourceCode.Contains("#")) then
        []
    else
        let doubleQuoteChar = '"'
        let backSlashChar = '\\'
        let isDoubleQuoteChar = (=) doubleQuoteChar
        let isBackSlashChar = (=) backSlashChar
        let isNoBackSlashChar v = v <> backSlashChar

        let tripleQuotes =
            (doubleQuoteChar, doubleQuoteChar, doubleQuoteChar)

        // remove `"*"` and `"""*"""` in source code
        let removeStringsIn (source: string) =
            let sb = StringBuilder(source.Length)
            let appendChar (v: char) = sb.Append(v) |> ignore
            let sourceLength = String.length source
            let lastIndex = sourceLength - 3
            let mutable currentState = Normal

            // Go over the source code char per char
            // Detect when we are inside a string or triple quote string
            // When not inside a string, collect the characters.
            for idx in [ 0 .. (sourceLength - 1) ] do
                let zero = source.[idx]

                if idx < 2 then
                    match currentState with
                    | Normal when (isDoubleQuoteChar zero) -> currentState <- InsideString
                    | Normal -> appendChar source.[idx]
                    | _ -> ()

                elif idx < lastIndex then
                    let minusTwo = source.[idx - 2]
                    let minusOne = source.[idx - 1]
                    let plusOne = source.[idx + 1]
                    let plusTwo = source.[idx + 2]

                    match currentState with
                    | Normal ->
                        if (zero, plusOne, plusTwo) = tripleQuotes
                        then currentState <- InsideTripleQuoteString idx
                        elif zero = doubleQuoteChar
                        then currentState <- InsideString
                        else appendChar zero

                    | InsideString ->
                        if isDoubleQuoteChar zero then
                            if isBackSlashChar minusOne
                               && isNoBackSlashChar minusTwo then
                                () // escaped quote
                            else
                                currentState <- Normal

                    | InsideTripleQuoteString startIndex ->
                        if (startIndex + 2 < idx)
                           && (minusTwo, minusOne, zero) = tripleQuotes then
                            // check if there is no backslash before the first `"` of `"""`
                            if (startIndex - 1) > 0 then
                                let minusThree = source.[idx - 3]
                                if isNoBackSlashChar minusThree then currentState <- Normal
                            else
                                currentState <- Normal
                else
                    match currentState with
                    | Normal -> appendChar source.[idx]
                    | _ -> ()

            sb.ToString()

        let processLine (content: string)
                        (trimmed: string)
                        (lineNumber: int)
                        (fullMatchedLength: int)
                        (offset: int)
                        : Token list =
            let contentLength = String.length content

            let tokens =
                tokenize [] (trimmed.Substring(contentLength))

            tokens
            |> List.map (fun t ->
                let info =
                    { t.TokenInfo with
                          LeftColumn = t.TokenInfo.LeftColumn + contentLength
                          RightColumn = t.TokenInfo.RightColumn + contentLength }

                { t with
                      LineNumber = lineNumber
                      TokenInfo = info })
            |> fun rest ->
                (createHashToken lineNumber content fullMatchedLength offset)
                :: rest

        let findDefineInLine (idx: int) (line: string): Token list =
            let lineNumber = idx + 1
            let fullMatchedLength = String.length line
            let trimmed = line.TrimStart()

            if trimmed.Contains("#") then
                let offset =
                    String.length line - String.length trimmed

                if trimmed.StartsWith("#if") then
                    processLine "#if" trimmed lineNumber fullMatchedLength offset
                elif trimmed.StartsWith("#elseif") then
                    processLine "#elseif" trimmed lineNumber fullMatchedLength offset
                elif trimmed.StartsWith("#else") then
                    processLine "#else" trimmed lineNumber fullMatchedLength offset
                elif trimmed.StartsWith("#endif") then
                    processLine "#endif" trimmed lineNumber fullMatchedLength offset
                else
                    []
            else
                []

        let sourceWithoutStrings = removeStringsIn sourceCode

        if not (sourceWithoutStrings.Contains("#")) then
            []
        else
            sourceWithoutStrings
            |> String.normalizeThenSplitNewLine
            |> Array.mapi findDefineInLine
            |> Array.toList
            |> List.concat

and tokenize defines (content: string): Token list =
    let sourceTokenizer =
        FSharpSourceTokenizer(defines, Some "/tmp.fsx")

    let lines =
        String.normalizeThenSplitNewLine content
        |> Array.toList

    let tokens =
        tokenizeLines sourceTokenizer lines FSharpTokenizerLexState.Initial
        |> List.filter (fun t -> t.TokenInfo.TokenName <> "INACTIVECODE")

    let existingLines =
        tokens
        |> List.map (fun t -> t.LineNumber)
        |> List.distinct

    let combined =
        if content.Contains("#") then
            let hashes = getTokenizedHashes content

            let filteredHashes =
                hashes
                |> List.filter (fun t -> not (List.contains t.LineNumber existingLines))
            // filter hashes that are present in source code parsed by the Tokenizer.
            tokens @ filteredHashes
            |> List.sortBy (fun t -> t.LineNumber, t.TokenInfo.LeftColumn)
        else
            tokens

    combined

let getDefines sourceCode =
    getTokenizedHashes sourceCode
    |> List.filter (fun { TokenInfo = { TokenName = tn } } -> tn = "IDENT")
    |> List.map (fun t -> t.Content)
    |> List.distinct

let getDefineExprs sourceCode =
    let parseHashContent tokens =
        let allowedContent = set [ "||"; "&&"; "!"; "("; ")" ]

        tokens
        |> Seq.filter (fun t ->
            t.TokenInfo.TokenName = "IDENT"
            || Set.contains t.Content allowedContent)
        |> Seq.map (fun t -> t.Content)
        |> Seq.toList
        |> BoolExprParser.parse

    let tokens = getTokenizedHashes sourceCode

    let tokensByLine =
        tokens
        |> List.groupBy (fun t -> t.LineNumber)
        |> List.sortBy fst

    let result =
        (([], []), tokensByLine)
        ||> List.fold (fun (contextExprs, exprAcc) (_, lineTokens) ->
                let contextExpr e =
                    e :: contextExprs
                    |> List.reduce (fun x y -> BoolExpr.And(x, y))

                let t =
                    lineTokens
                    |> Seq.tryFind (fun x -> x.TokenInfo.TokenName = "HASH_IF")

                match t |> Option.map (fun x -> x.Content) with
                | Some "#if" ->
                    parseHashContent lineTokens
                    |> Option.map (fun e -> e :: contextExprs, contextExpr e :: exprAcc)
                    |> Option.defaultValue (contextExprs, exprAcc)
                | Some "#else" ->
                    contextExprs,
                    BoolExpr.Not
                        (contextExprs
                         |> List.reduce (fun x y -> BoolExpr.And(x, y)))
                    :: exprAcc
                | Some "#endif" -> List.tail contextExprs, exprAcc
                | _ -> contextExprs, exprAcc)
        |> snd
        |> List.rev

    result

let getOptimizedDefinesSets sourceCode =
    let maxSteps = FormatConfig.satSolveMaxStepsMaxSteps

    match getDefineExprs sourceCode
          |> BoolExpr.mergeBoolExprs maxSteps
          |> List.map snd with
    | [] -> [ [] ]
    | xs -> xs

let private getRangeBetween name startToken endToken =
    let l = startToken.TokenInfo.LeftColumn
    let r = endToken.TokenInfo.RightColumn

    let start =
        FSharp.Compiler.Range.mkPos startToken.LineNumber l

    let endR =
        FSharp.Compiler.Range.mkPos endToken.LineNumber (if l = r then r + 1 else r)

    FSharp.Compiler.Range.mkRange name start endR

let private getRangeForSingleToken name token =
    let l = token.TokenInfo.LeftColumn
    let r = l + token.TokenInfo.FullMatchedLength

    let start =
        FSharp.Compiler.Range.mkPos token.LineNumber l

    let endR =
        FSharp.Compiler.Range.mkPos token.LineNumber r

    FSharp.Compiler.Range.mkRange name start endR

let private hasOnlySpacesAndLineCommentsOnLine lineNumber tokens =
    if List.isEmpty tokens then
        false
    else
        tokens
        |> List.filter (fun t -> t.LineNumber = lineNumber)
        |> List.forall (fun t ->
            t.TokenInfo.Tag = whiteSpaceTag
            || t.TokenInfo.Tag = lineCommentTag)

let private getContentFromTokens tokens =
    tokens
    |> List.map (fun t -> t.Content)
    |> String.concat String.Empty

let private keywordTrivia =
    [ "IF"
      "ELIF"
      "ELSE"
      "THEN"
      "OVERRIDE"
      "MEMBER"
      "DEFAULT"
      "ABSTRACT"
      "KEYWORD_STRING"
      "QMARK"
      "IN" ]

let private numberTrivia =
    [ "UINT8"
      "INT8"
      "UINT16"
      "INT16"
      "UINT32"
      "INT32"
      "UINT64"
      "INT64"
      "IEEE32"
      "DECIMAL"
      "IEEE64"
      "BIGNUM"
      "NATIVEINT"
      "UNATIVEINT" ]

let private isOperatorOrKeyword ({ TokenInfo = { CharClass = cc } }) =
    cc = FSharpTokenCharKind.Keyword
    || cc = FSharpTokenCharKind.Operator

let private isNumber ({ TokenInfo = tn }) =
    tn.ColorClass = FSharpTokenColorKind.Number
    && List.contains tn.TokenName numberTrivia

let private identIsDecompiledOperator (token: Token) =
    let decompiledName =
        FSharp.Compiler.PrettyNaming.DecompileOpName token.Content

    token.TokenInfo.TokenName = "IDENT"
    && decompiledName <> token.Content

let ``only whitespaces were found in the remainder of the line`` lineNumber tokens =
    tokens
    |> List.exists (fun t ->
        t.LineNumber = lineNumber
        && t.TokenInfo.Tag <> whiteSpaceTag)
    |> not

let rec private getTriviaFromTokensThemSelves (allTokens: Token list) (tokens: Token list) foundTrivia =
    match tokens with
    | headToken :: rest when (headToken.TokenInfo.Tag = lineCommentTag) ->
        let lineCommentTokens =
            Seq.zip
                rest
                (headToken :: rest
                 |> List.map (fun x -> x.LineNumber))
            |> Seq.takeWhile (fun (t, currentLineNumber) ->
                t.TokenInfo.Tag = lineCommentTag
                && t.LineNumber <= (currentLineNumber + 1))
            |> Seq.map fst
            |> Seq.toList

        let comment =
            headToken
            |> List.prependItem lineCommentTokens
            |> List.groupBy (fun t -> t.LineNumber)
            |> List.map (snd >> getContentFromTokens)
            |> String.concat "\n"

        let nextTokens =
            List.length lineCommentTokens
            |> fun length -> List.skip length rest

        let range =
            let lastToken = List.tryLast lineCommentTokens
            getRangeBetween "line comment" headToken (Option.defaultValue headToken lastToken)

        let info =
            let toLineComment =
                allTokens
                |> List.exists (fun t ->
                    t.LineNumber = headToken.LineNumber
                    && t.TokenInfo.Tag <> whiteSpaceTag
                    && t.TokenInfo.RightColumn < headToken.TokenInfo.LeftColumn)
                |> fun e -> if e then LineCommentAfterSourceCode else LineCommentOnSingleLine

            let comment = toLineComment comment |> Comment

            Trivia.Create comment range
            |> List.appendItem foundTrivia

        getTriviaFromTokensThemSelves allTokens nextTokens info

    | headToken :: rest when (headToken.TokenInfo.TokenName = "COMMENT") ->
        let blockCommentTokens =
            rest
            |> List.takeWhileState (fun depth t ->
                let newDepth =
                    match t.Content with
                    | "(*" -> depth + 1
                    | "*)" -> depth - 1
                    | _ -> depth

                newDepth, t.TokenInfo.TokenName = "COMMENT" && depth > 0) 1

        let comment =
            let groupedByLineNumber =
                headToken
                |> List.prependItem blockCommentTokens
                |> List.groupBy (fun t -> t.LineNumber)

            let newLines =
                let (min, _) = List.minBy fst groupedByLineNumber
                let (max, _) = List.maxBy fst groupedByLineNumber

                [ min .. max ]
                |> List.filter (fun l -> not (List.exists (fst >> ((=) l)) groupedByLineNumber))
                |> List.map (fun l -> l, String.Empty)

            groupedByLineNumber
            |> List.map (fun (l, g) -> l, getContentFromTokens g)
            |> (@) newLines
            |> List.sortBy fst
            |> List.map snd
            |> String.concat Environment.NewLine
            |> String.normalizeNewLine

        let nextTokens =
            List.length blockCommentTokens
            |> fun length -> List.skip length rest

        let range =
            let lastToken = List.last blockCommentTokens
            getRangeBetween "block comment" headToken lastToken

        let info =
            Trivia.Create (Comment(BlockComment(comment, false, false))) range
            |> List.prependItem foundTrivia

        getTriviaFromTokensThemSelves allTokens nextTokens info

    | headToken :: rest when (isOperatorOrKeyword headToken
                              && List.exists (fun k -> headToken.TokenInfo.TokenName = k) keywordTrivia) ->
        let range =
            getRangeBetween "keyword" headToken headToken

        let info =
            Trivia.Create (Keyword(headToken)) range
            |> List.prependItem foundTrivia

        getTriviaFromTokensThemSelves allTokens rest info

    | headToken :: rest when (headToken.TokenInfo.TokenName = "HASH_IF") ->
        let directiveTokens =
            rest
            |> List.filter (fun r -> r.LineNumber = headToken.LineNumber)
            |> fun others -> List.prependItem others headToken

        let directiveContent =
            directiveTokens
            |> List.map (fun t -> t.Content)
            |> String.concat String.empty

        let range =
            getRangeBetween "directive" headToken (List.last directiveTokens)

        let info =
            Trivia.Create (Directive(directiveContent)) range
            |> List.prependItem foundTrivia

        let nextRest =
            match rest with
            | [] -> []
            | _ -> List.skip (List.length directiveTokens - 1) rest

        getTriviaFromTokensThemSelves allTokens nextRest info

    | head :: rest when (head.TokenInfo.TokenName = "STRING_TEXT") ->
        let stringTokens =
            rest
            |> List.takeWhile (fun { TokenInfo = { TokenName = tn } } -> tn = "STRING_TEXT")
            |> fun others ->
                let length = List.length others
                let closingQuote = rest.[length]

                [ yield head
                  yield! others
                  yield closingQuote ]

        let stringContent =
            let builder = StringBuilder()

            stringTokens
            |> List.fold (fun (b: StringBuilder, currentLine) st ->
                if currentLine <> st.LineNumber then
                    let delta = st.LineNumber - currentLine

                    [ 1 .. delta ]
                    |> List.iter (fun _ -> b.Append("\n") |> ignore)

                    b.Append(st.Content), st.LineNumber
                else
                    b.Append(st.Content), st.LineNumber) (builder, head.LineNumber)
            |> fst
            |> fun b -> b.ToString()

        let lastToken =
            List.tryLast stringTokens
            |> Option.defaultValue head

        let range =
            getRangeBetween "string content" head lastToken

        let info =
            Trivia.Create (StringContent(stringContent)) range
            |> List.prependItem foundTrivia

        let nextRest =
            match rest with
            | [] -> []
            | _ -> List.skip (List.length stringTokens - 1) rest

        getTriviaFromTokensThemSelves allTokens nextRest info

    | minus :: head :: rest when (minus.TokenInfo.TokenName = "MINUS"
                                  && isNumber head) ->
        let range = getRangeBetween "number" minus head

        let info =
            Trivia.Create (Number(minus.Content + head.Content)) range
            |> List.prependItem foundTrivia

        getTriviaFromTokensThemSelves allTokens rest info

    | head :: rest when (isNumber head) ->
        let range = getRangeForSingleToken "number" head

        let info =
            Trivia.Create (Number(head.Content)) range
            |> List.prependItem foundTrivia

        getTriviaFromTokensThemSelves allTokens rest info

    | head :: rest when (identIsDecompiledOperator head) ->
        let range =
            getRangeBetween "operator as word" head head

        let info =
            Trivia.Create (IdentOperatorAsWord head.Content) range
            |> List.prependItem foundTrivia

        getTriviaFromTokensThemSelves allTokens rest info

    | head :: rest when (head.TokenInfo.TokenName = "IDENT"
                         && head.Content.StartsWith("``")
                         && head.Content.EndsWith("``")) ->
        let range =
            getRangeBetween "ident between ``" head head

        let info =
            Trivia.Create (IdentBetweenTicks(head.Content)) range
            |> List.prependItem foundTrivia

        getTriviaFromTokensThemSelves allTokens rest info

    | head :: rest when (head.TokenInfo.TokenName = "CHAR") ->
        let range =
            getRangeBetween head.TokenInfo.TokenName head head

        let info =
            Trivia.Create (CharContent(head.Content)) range
            |> List.prependItem foundTrivia

        getTriviaFromTokensThemSelves allTokens rest info

    | _ :: rest -> getTriviaFromTokensThemSelves allTokens rest foundTrivia

    | [] -> foundTrivia

let private createNewLine lineNumber =
    let pos = FSharp.Compiler.Range.mkPos lineNumber 0

    let range =
        FSharp.Compiler.Range.mkRange "newline" pos pos

    { Item = Newline; Range = range }

let private findEmptyNewlinesInTokens (tokens: Token list) (ignoreRanges: FSharp.Compiler.Range.range list) =
    let nonWhitespaceLines =
        tokens
        |> List.choose (fun t -> if t.TokenInfo.Tag <> whiteSpaceTag then Some t.LineNumber else None)
        |> List.distinct

    let ignoreRanges =
        ignoreRanges
        |> List.collect (fun r -> [ r.StartLine .. r.EndLine ])

    let lastLineWithContent =
        List.tryLast nonWhitespaceLines
        |> Option.defaultValue 1

    [ 1 .. lastLineWithContent ]
    |> List.except (nonWhitespaceLines @ ignoreRanges)
    |> List.map createNewLine

let getTriviaFromTokens (tokens: Token list) =
    let fromTokens =
        getTriviaFromTokensThemSelves tokens tokens []

    let blockComments =
        fromTokens
        |> List.choose (fun tc ->
            match tc.Item with
            | Comment (BlockComment _) -> Some tc.Range
            | _ -> None)

    let isMultilineString s =
        String.split StringSplitOptions.None [| "\n" |] s
        |> (Seq.isEmpty >> not)

    let multilineStrings =
        fromTokens
        |> List.choose (fun tc ->
            match tc.Item with
            | StringContent (sc) when (isMultilineString sc) -> Some tc.Range
            | _ -> None)

    let newLines =
        findEmptyNewlinesInTokens tokens (blockComments @ multilineStrings)

    fromTokens @ newLines
    |> List.sortBy (fun t -> t.Range.StartLine, t.Range.StartColumn)

let private tokenNames =
    [ "LBRACE"
      "RBRACE"
      "LPAREN"
      "RPAREN"
      "LBRACK"
      "RBRACK"
      "LBRACK_BAR"
      "BAR_RBRACK"
      "EQUALS"
      "IF"
      "THEN"
      "ELSE"
      "ELIF"
      "BAR"
      "RARROW"
      "TRY"
      "FINALLY"
      "WITH"
      "MEMBER"
      "AND_BANG"
      "FUNCTION"
      "IN" ]

let private tokenKinds = [ FSharpTokenCharKind.Operator ]

let internal getFsToken tokenName =
    match tokenName with
    | "LBRACE" -> LBRACE
    | "RBRACE" -> RBRACE
    | "LPAREN" -> LPAREN
    | "RPAREN" -> RPAREN
    | "LBRACK" -> LBRACK
    | "RBRACK" -> RBRACK
    | "LBRACK_BAR" -> LBRACK_BAR
    | "BAR_RBRACK" -> BAR_RBRACK
    | "EQUALS" -> EQUALS
    | "IF" -> IF
    | "THEN" -> THEN
    | "ELSE" -> ELSE
    | "ELIF" -> ELIF
    | "BAR" -> BAR
    | "RARROW" -> RARROW
    | "TRY" -> TRY
    | "FINALLY" -> FINALLY
    | "WITH" -> WITH
    | "MEMBER" -> MEMBER
    | "AND_BANG" -> AND_BANG
    | "PERCENT_OP" -> PERCENT_OP
    | "AMP" -> AMP
    | "INFIX_BAR_OP" -> INFIX_BAR_OP
    | "INFIX_COMPARE_OP" -> INFIX_COMPARE_OP
    | "LESS" -> LESS
    | "AMP_AMP" -> AMP_AMP
    | "GREATER" -> GREATER
    | "INFIX_STAR_DIV_MOD_OP" -> INFIX_STAR_DIV_MOD_OP
    | "DELAYED" -> DELAYED
    | "PLUS_MINUS_OP" -> PLUS_MINUS_OP
    | "QMARK" -> QMARK
    | "MINUS" -> MINUS
    | "COLON_QMARK" -> COLON_QMARK
    | "DOT_DOT" -> DOT_DOT
    | "INT32_DOT_DOT" -> INT32_DOT_DOT
    | "COLON_EQUALS" -> COLON_EQUALS
    | "PREFIX_OP" -> PREFIX_OP
    | "INFIX_AMP_OP" -> INFIX_AMP_OP
    | "COLON_QMARK_GREATER" -> COLON_QMARK_GREATER
    | "COLON_COLON" -> COLON_COLON
    | "COLON_GREATER" -> COLON_GREATER
    | "DOT_DOT_HAT" -> DOT_DOT_HAT
    | "BAR_BAR" -> BAR_BAR
    | "INFIX_STAR_STAR_OP" -> INFIX_STAR_STAR_OP
    | "FUNCTION" -> FUNCTION
    | "LPAREN_STAR_RPAREN" -> LPAREN_STAR_RPAREN
    | "IN" -> IN
    | _ -> failwithf "was not expecting token %s" tokenName

let getTriviaNodesFromTokens (tokens: Token list) =
    tokens
    |> List.filter (fun t ->
        List.exists (fun tn -> tn = t.TokenInfo.TokenName) tokenNames
        || List.exists (fun tk -> tk = t.TokenInfo.CharClass) tokenKinds)
    |> List.map (fun t ->
        let range =
            getRangeBetween t.TokenInfo.TokenName t t

        TriviaNodeAssigner(TriviaNodeType.Token(getFsToken t.TokenInfo.TokenName, t), range))
