// Codemap JS/TS analyzer, invoked from .NET via Jering.Javascript.NodeJS.
// Uses the TypeScript Compiler API (ts.createProgram) for .ts/.tsx/.jsx files and Acorn for plain .js.
// Emits nodes/edges/HTTP call sites as JSON matching the CodeNode/CodeEdge shape on the .NET side.
//
// Node ids: modules are file-relative paths ("src/api/client.ts"); classes/functions/interfaces/enums
// are "<modulePath>#<Name>". Namespace = the module's directory path. Same-module calls become Calls
// edges; cross-module calls become lower-confidence References edges to the imported module node.
'use strict';

const fs = require('fs');
const path = require('path');

module.exports = (callback, optionsJson) => {
    try {
        const options = JSON.parse(optionsJson);
        callback(null, analyze(options));
    } catch (err) {
        callback(err);
    }
};

function analyze(options) {
    const out = { nodes: [], edges: [], callSites: [], warnings: [] };
    const rootPath = options.rootPath;
    const tsFiles = (options.tsFiles || []);
    const jsFiles = (options.jsFiles || []);

    if (tsFiles.length > 0) analyzeTypeScript(tsFiles, rootPath, out);
    if (jsFiles.length > 0) analyzeJavaScript(jsFiles, rootPath, out);

    dedupeNodes(out);
    return out;
}

function rel(rootPath, filePath) {
    return path.relative(rootPath, filePath).split(path.sep).join('/');
}

function dirOf(relPath) {
    const dir = path.posix.dirname(relPath);
    return dir === '.' ? '' : dir;
}

function dedupeNodes(out) {
    const seen = new Set();
    out.nodes = out.nodes.filter(n => {
        if (seen.has(n.id)) return false;
        seen.add(n.id);
        return true;
    });
}

// Resolves an import specifier ("./api/client") to a scanned module's relative path, best effort.
function resolveImport(fromRelPath, specifier, knownModules) {
    if (!specifier.startsWith('.')) return null; // bare specifier = external package
    const base = path.posix.normalize(path.posix.join(dirOf(fromRelPath), specifier));
    const candidates = [
        base,
        base + '.ts', base + '.tsx', base + '.js', base + '.jsx', base + '.mjs', base + '.cjs',
        base + '/index.ts', base + '/index.tsx', base + '/index.js',
        // TS convention: import "./x.js" resolves to source file "./x.ts"
        base.replace(/\.js$/, '.ts'), base.replace(/\.jsx$/, '.tsx'),
    ];
    for (const candidate of candidates) {
        if (knownModules.has(candidate)) return candidate;
    }
    return null;
}

function normalizeHttpMethod(name) {
    const methods = { get: 'GET', post: 'POST', put: 'PUT', delete: 'DELETE', patch: 'PATCH', head: 'HEAD' };
    return methods[name.toLowerCase()] || null;
}

// ---------------------------------------------------------------------------
// TypeScript / TSX (TypeScript Compiler API)
// ---------------------------------------------------------------------------

function analyzeTypeScript(files, rootPath, out) {
    let ts;
    try {
        ts = require('typescript');
    } catch {
        out.warnings.push('JS analysis: the "typescript" package is not installed in the analyzer scripts folder; .ts/.tsx files were skipped.');
        return;
    }

    const normalized = files.map(f => path.resolve(f));
    const program = ts.createProgram(normalized, {
        allowJs: true,
        checkJs: false,
        noEmit: true,
        jsx: ts.JsxEmit.Preserve,
        target: ts.ScriptTarget.Latest,
        moduleResolution: ts.ModuleResolutionKind.Bundler,
    });

    const wanted = new Set(normalized.map(f => path.normalize(f).toLowerCase()));
    const knownModules = new Set(files.map(f => rel(rootPath, f)));

    for (const sourceFile of program.getSourceFiles()) {
        if (sourceFile.isDeclarationFile) continue;
        if (!wanted.has(path.normalize(sourceFile.fileName).toLowerCase())) continue;
        try {
            walkTsFile(ts, sourceFile, rootPath, knownModules, out);
        } catch (err) {
            out.warnings.push(`JS analysis: failed to analyze '${sourceFile.fileName}': ${err.message}`);
        }
    }
}

function walkTsFile(ts, sourceFile, rootPath, knownModules, out) {
    const moduleId = rel(rootPath, sourceFile.fileName);
    const language = /\.tsx?$/.test(sourceFile.fileName) ? 'TypeScript' : 'JavaScript';
    const namespace = dirOf(moduleId);

    const lineOf = node => sourceFile.getLineAndCharacterOfPosition(node.getStart(sourceFile)).line + 1;

    out.nodes.push({
        id: moduleId,
        displayName: path.posix.basename(moduleId),
        kind: 'Module',
        language,
        namespace,
        members: [],
        sourceFile: moduleId,
        lineNumber: 1,
    });

    const imports = new Map();       // local binding name -> { module, exportedName }
    const topLevelFunctions = new Set();
    const sameFileClasses = new Set();

    // Pass 1: top-level declarations (nodes + import table).
    for (const statement of sourceFile.statements) {
        if (ts.isImportDeclaration(statement) && ts.isStringLiteral(statement.moduleSpecifier)) {
            const target = resolveImport(moduleId, statement.moduleSpecifier.text, knownModules);
            if (target) {
                out.edges.push({ fromId: moduleId, toId: target, kind: 'References', detail: 'import' });
                const clause = statement.importClause;
                if (clause) {
                    if (clause.name) imports.set(clause.name.text, { module: target, exportedName: 'default' });
                    const bindings = clause.namedBindings;
                    if (bindings && ts.isNamedImports(bindings)) {
                        for (const spec of bindings.elements) {
                            imports.set(spec.name.text, { module: target, exportedName: (spec.propertyName || spec.name).text });
                        }
                    } else if (bindings && ts.isNamespaceImport(bindings)) {
                        imports.set(bindings.name.text, { module: target, exportedName: '*' });
                    }
                }
            }
        } else if (ts.isClassDeclaration(statement) && statement.name) {
            sameFileClasses.add(statement.name.text);
        } else if (ts.isFunctionDeclaration(statement) && statement.name) {
            topLevelFunctions.add(statement.name.text);
        }
    }

    for (const statement of sourceFile.statements) {
        if (ts.isClassDeclaration(statement) && statement.name) {
            emitTsClass(ts, statement, sourceFile, moduleId, language, namespace, imports, out, lineOf);
        } else if (ts.isInterfaceDeclaration(statement)) {
            out.nodes.push(tsSimpleNode(statement.name.text, 'Interface'));
            for (const heritage of statement.heritageClauses || []) {
                for (const type of heritage.types) {
                    const baseEdge = resolveTsTypeName(ts, type.expression, moduleId, imports, sameFileClasses, knownModules);
                    if (baseEdge) out.edges.push({ fromId: `${moduleId}#${statement.name.text}`, toId: baseEdge, kind: 'Inherits', detail: null });
                }
            }
        } else if (ts.isEnumDeclaration(statement)) {
            const members = statement.members.map(m => ({
                signature: m.name.getText(sourceFile),
                returnOrType: statement.name.text,
                isStatic: true,
            }));
            out.nodes.push({ ...tsSimpleNode(statement.name.text, 'Enum'), members });
        } else if (ts.isFunctionDeclaration(statement) && statement.name) {
            const params = statement.parameters.map(p => p.name.getText(sourceFile)).join(', ');
            out.nodes.push({
                ...tsSimpleNode(statement.name.text, 'Function'),
                members: [{
                    signature: `${statement.name.text}(${params})`,
                    returnOrType: statement.type ? statement.type.getText(sourceFile) : '',
                    isStatic: false,
                }],
            });
        }

        function tsSimpleNode(name, kind) {
            return {
                id: `${moduleId}#${name}`,
                displayName: name,
                kind,
                language,
                namespace,
                members: [],
                sourceFile: moduleId,
                lineNumber: lineOf(statement),
            };
        }
    }

    // Pass 2: call expressions (call edges + HTTP call sites), tracked with a container stack.
    const containerStack = [moduleId];

    const visit = node => {
        let pushed = false;
        if ((ts.isClassDeclaration(node) || ts.isFunctionDeclaration(node)) && node.name) {
            containerStack.push(`${moduleId}#${node.name.text}`);
            pushed = true;
        }

        if (ts.isCallExpression(node)) {
            handleTsCall(node);
        }

        ts.forEachChild(node, visit);
        if (pushed) containerStack.pop();
    };
    visit(sourceFile);

    function handleTsCall(node) {
        const container = containerStack[containerStack.length - 1];

        // HTTP call site detection: fetch(...), axios.get(...), $http.get(...)
        const http = matchHttpCall(ts, node, sourceFile);
        if (http) {
            out.callSites.push({
                nodeId: container,
                httpMethod: http.method,
                url: http.url,
                sourceFile: moduleId,
                lineNumber: lineOf(node),
            });
        }

        if (ts.isIdentifier(node.expression)) {
            const name = node.expression.text;
            if (topLevelFunctions.has(name)) {
                // Same-module call — confident.
                out.edges.push({ fromId: container, toId: `${moduleId}#${name}`, kind: 'Calls', detail: name });
            } else if (imports.has(name)) {
                // Cross-module call — dynamically typed, lower confidence: References to the module.
                const binding = imports.get(name);
                out.edges.push({ fromId: container, toId: binding.module, kind: 'References', detail: `${name}()` });
            }
        }
    }
}

function emitTsClass(ts, statement, sourceFile, moduleId, language, namespace, imports, out, lineOf) {
    const name = statement.name.text;
    const id = `${moduleId}#${name}`;
    const members = [];

    for (const member of statement.members) {
        const isStatic = (ts.getCombinedModifierFlags(member) & ts.ModifierFlags.Static) !== 0;
        if (ts.isMethodDeclaration(member) && member.name) {
            const params = member.parameters.map(p => p.name.getText(sourceFile)).join(', ');
            members.push({
                signature: `${member.name.getText(sourceFile)}(${params})`,
                returnOrType: member.type ? member.type.getText(sourceFile) : '',
                isStatic,
            });
        } else if (ts.isPropertyDeclaration(member) && member.name) {
            members.push({
                signature: member.name.getText(sourceFile),
                returnOrType: member.type ? member.type.getText(sourceFile) : '',
                isStatic,
            });
        } else if (ts.isConstructorDeclaration(member)) {
            const params = member.parameters.map(p => p.name.getText(sourceFile)).join(', ');
            members.push({ signature: `constructor(${params})`, returnOrType: 'ctor', isStatic: false });
        }
    }

    out.nodes.push({
        id,
        displayName: name,
        kind: 'Class',
        language,
        namespace,
        members,
        sourceFile: moduleId,
        lineNumber: lineOf(statement),
    });

    for (const heritage of statement.heritageClauses || []) {
        const kind = heritage.token === ts.SyntaxKind.ExtendsKeyword ? 'Inherits' : 'Implements';
        for (const type of heritage.types) {
            if (!ts.isIdentifier(type.expression)) continue;
            const baseName = type.expression.text;
            const binding = imports.get(baseName);
            const toId = binding
                ? (binding.exportedName === '*' || binding.exportedName === 'default'
                    ? binding.module
                    : `${binding.module}#${binding.exportedName}`)
                : `${moduleId}#${baseName}`;
            out.edges.push({ fromId: id, toId, kind, detail: null });
        }
    }
}

function resolveTsTypeName(ts, expression, moduleId, imports, sameFileClasses, knownModules) {
    if (!ts.isIdentifier(expression)) return null;
    const name = expression.text;
    const binding = imports.get(name);
    if (binding) {
        return binding.exportedName === '*' || binding.exportedName === 'default'
            ? binding.module
            : `${binding.module}#${binding.exportedName}`;
    }
    return `${moduleId}#${name}`;
}

// Extracts { method, url } if the call is fetch(...) / axios.verb(...) / $http.verb(...).
// URL must be a string literal or a template literal; template substitutions become "{expr}" so
// they line up with route-parameter normalization on the .NET side. No data-flow analysis.
function matchHttpCall(ts, node, sourceFile) {
    const expr = node.expression;
    let method = null;
    let urlArg = null;

    if (ts.isIdentifier(expr) && expr.text === 'fetch' && node.arguments.length >= 1) {
        urlArg = node.arguments[0];
        method = 'GET';
        if (node.arguments.length >= 2 && ts.isObjectLiteralExpression(node.arguments[1])) {
            for (const prop of node.arguments[1].properties) {
                if (ts.isPropertyAssignment(prop)
                    && prop.name.getText(sourceFile) === 'method'
                    && (ts.isStringLiteral(prop.initializer) || ts.isNoSubstitutionTemplateLiteral(prop.initializer))) {
                    method = prop.initializer.text.toUpperCase();
                }
            }
        }
    } else if (ts.isPropertyAccessExpression(expr)
        && ts.isIdentifier(expr.expression)
        && (expr.expression.text === 'axios' || expr.expression.text === '$http')
        && node.arguments.length >= 1) {
        method = normalizeHttpMethod(expr.name.text);
        urlArg = node.arguments[0];
    }

    if (!method || !urlArg) return null;
    const url = extractTsUrl(ts, urlArg, sourceFile);
    return url ? { method, url } : null;
}

function extractTsUrl(ts, node, sourceFile) {
    if (ts.isStringLiteral(node) || ts.isNoSubstitutionTemplateLiteral(node)) return node.text;
    if (ts.isTemplateExpression(node)) {
        let url = node.head.text;
        for (const span of node.templateSpans) {
            url += `{${span.expression.getText(sourceFile)}}` + span.literal.text;
        }
        return url;
    }
    return null;
}

// ---------------------------------------------------------------------------
// Plain JavaScript (Acorn, ESTree AST)
// ---------------------------------------------------------------------------

function analyzeJavaScript(files, rootPath, out) {
    let acorn, walk;
    try {
        acorn = require('acorn');
        walk = require('acorn-walk');
    } catch {
        out.warnings.push('JS analysis: the "acorn" package is not installed in the analyzer scripts folder; .js files were skipped.');
        return;
    }

    const knownModules = new Set(files.map(f => rel(rootPath, f)));

    for (const file of files) {
        try {
            walkJsFile(acorn, walk, file, rootPath, knownModules, out);
        } catch (err) {
            out.warnings.push(`JS analysis: failed to parse '${file}': ${err.message}`);
        }
    }
}

function walkJsFile(acorn, walk, file, rootPath, knownModules, out) {
    const source = fs.readFileSync(file, 'utf8');
    const moduleId = rel(rootPath, file);
    const namespace = dirOf(moduleId);

    const ast = acorn.parse(source, {
        ecmaVersion: 'latest',
        sourceType: 'module',
        locations: true,
        allowHashBang: true,
    });

    out.nodes.push({
        id: moduleId,
        displayName: path.posix.basename(moduleId),
        kind: 'Module',
        language: 'JavaScript',
        namespace,
        members: [],
        sourceFile: moduleId,
        lineNumber: 1,
    });

    const imports = new Map();
    const topLevelFunctions = new Set();

    const unwrapExport = statement =>
        (statement.type === 'ExportNamedDeclaration' || statement.type === 'ExportDefaultDeclaration') && statement.declaration
            ? statement.declaration
            : statement;

    // Pass 1: top-level declarations.
    for (const raw of ast.body) {
        const statement = unwrapExport(raw);
        if (raw.type === 'ImportDeclaration' && typeof raw.source.value === 'string') {
            const target = resolveImport(moduleId, raw.source.value, knownModules);
            if (target) {
                out.edges.push({ fromId: moduleId, toId: target, kind: 'References', detail: 'import' });
                for (const spec of raw.specifiers) {
                    if (spec.type === 'ImportSpecifier') imports.set(spec.local.name, { module: target, exportedName: spec.imported.name || spec.imported.value });
                    else if (spec.type === 'ImportDefaultSpecifier') imports.set(spec.local.name, { module: target, exportedName: 'default' });
                    else if (spec.type === 'ImportNamespaceSpecifier') imports.set(spec.local.name, { module: target, exportedName: '*' });
                }
            }
        } else if (statement.type === 'FunctionDeclaration' && statement.id) {
            topLevelFunctions.add(statement.id.name);
            const params = statement.params.map(p => paramName(p)).join(', ');
            out.nodes.push({
                id: `${moduleId}#${statement.id.name}`,
                displayName: statement.id.name,
                kind: 'Function',
                language: 'JavaScript',
                namespace,
                members: [{ signature: `${statement.id.name}(${params})`, returnOrType: '', isStatic: false }],
                sourceFile: moduleId,
                lineNumber: statement.loc.start.line,
            });
        } else if (statement.type === 'ClassDeclaration' && statement.id) {
            emitJsClass(statement, moduleId, namespace, imports, out);
        }
    }

    // Pass 2: calls + HTTP call sites, with ancestor-based container resolution.
    walk.ancestor(ast, {
        CallExpression(node, _state, ancestors) {
            const container = jsContainerFor(ancestors, moduleId);

            const http = matchJsHttpCall(node);
            if (http) {
                out.callSites.push({
                    nodeId: container,
                    httpMethod: http.method,
                    url: http.url,
                    sourceFile: moduleId,
                    lineNumber: node.loc.start.line,
                });
            }

            if (node.callee.type === 'Identifier') {
                const name = node.callee.name;
                if (topLevelFunctions.has(name)) {
                    out.edges.push({ fromId: container, toId: `${moduleId}#${name}`, kind: 'Calls', detail: name });
                } else if (imports.has(name)) {
                    out.edges.push({ fromId: container, toId: imports.get(name).module, kind: 'References', detail: `${name}()` });
                }
            }
        },
    });

    function paramName(p) {
        if (p.type === 'Identifier') return p.name;
        if (p.type === 'AssignmentPattern' && p.left.type === 'Identifier') return p.left.name;
        if (p.type === 'RestElement' && p.argument.type === 'Identifier') return '...' + p.argument.name;
        return '_';
    }

    function emitJsClass(statement, moduleId, namespace, imports, out) {
        const name = statement.id.name;
        const id = `${moduleId}#${name}`;
        const members = [];
        for (const member of statement.body.body || []) {
            if (member.type === 'MethodDefinition' && member.key.type === 'Identifier') {
                const params = (member.value.params || []).map(p => paramName(p)).join(', ');
                members.push({
                    signature: `${member.key.name}(${params})`,
                    returnOrType: member.kind === 'constructor' ? 'ctor' : '',
                    isStatic: !!member.static,
                });
            } else if (member.type === 'PropertyDefinition' && member.key.type === 'Identifier') {
                members.push({ signature: member.key.name, returnOrType: '', isStatic: !!member.static });
            }
        }

        out.nodes.push({
            id,
            displayName: name,
            kind: 'Class',
            language: 'JavaScript',
            namespace,
            members,
            sourceFile: moduleId,
            lineNumber: statement.loc.start.line,
        });

        if (statement.superClass && statement.superClass.type === 'Identifier') {
            const baseName = statement.superClass.name;
            const binding = imports.get(baseName);
            const toId = binding
                ? (binding.exportedName === '*' || binding.exportedName === 'default'
                    ? binding.module
                    : `${binding.module}#${binding.exportedName}`)
                : `${moduleId}#${baseName}`;
            out.edges.push({ fromId: id, toId, kind: 'Inherits', detail: null });
        }
    }
}

function jsContainerFor(ancestors, moduleId) {
    for (let i = ancestors.length - 1; i >= 0; i--) {
        const node = ancestors[i];
        if ((node.type === 'ClassDeclaration' || node.type === 'FunctionDeclaration') && node.id) {
            return `${moduleId}#${node.id.name}`;
        }
    }
    return moduleId;
}

function matchJsHttpCall(node) {
    let method = null;
    let urlArg = null;

    if (node.callee.type === 'Identifier' && node.callee.name === 'fetch' && node.arguments.length >= 1) {
        urlArg = node.arguments[0];
        method = 'GET';
        const config = node.arguments[1];
        if (config && config.type === 'ObjectExpression') {
            for (const prop of config.properties) {
                if (prop.type === 'Property'
                    && ((prop.key.type === 'Identifier' && prop.key.name === 'method') || (prop.key.type === 'Literal' && prop.key.value === 'method'))
                    && prop.value.type === 'Literal'
                    && typeof prop.value.value === 'string') {
                    method = prop.value.value.toUpperCase();
                }
            }
        }
    } else if (node.callee.type === 'MemberExpression'
        && node.callee.object.type === 'Identifier'
        && (node.callee.object.name === 'axios' || node.callee.object.name === '$http')
        && node.callee.property.type === 'Identifier'
        && node.arguments.length >= 1) {
        method = normalizeHttpMethod(node.callee.property.name);
        urlArg = node.arguments[0];
    }

    if (!method || !urlArg) return null;
    const url = extractJsUrl(urlArg);
    return url ? { method, url } : null;
}

function extractJsUrl(node) {
    if (node.type === 'Literal' && typeof node.value === 'string') return node.value;
    if (node.type === 'TemplateLiteral') {
        let url = '';
        for (let i = 0; i < node.quasis.length; i++) {
            url += node.quasis[i].value.cooked;
            if (i < node.expressions.length) url += '{expr}';
        }
        return url;
    }
    return null;
}
