// =============================================================================
//  Gera a colecao do Bruno a partir do OpenAPI da API.
//
//  ===================== POR QUE UM GERADOR, E NAO UMA COLECAO A MAO =====================
//  Sao 97 operacoes. Colecao escrita a mao envelhece no primeiro endpoint novo, e o jeito de
//  descobrir e alguem tentar usar uma rota que mudou de forma ha tres semanas. O gerador le o
//  documento que o proprio codigo produz — entao a colecao nunca diverge da API por descuido,
//  so por nao ter sido regerada, que e uma falha visivel.
//  =======================================================================================
//
//  Uso (API no ar, em Development):
//    node ferramentas/bruno/gerar.mjs
//    node ferramentas/bruno/gerar.mjs --url http://localhost:5123 --saida ferramentas/bruno/nexora
//
//  Sem dependencia nenhuma: so `fetch` e `fs`, ambos nativos no Node 18+.
// =============================================================================

import { mkdirSync, writeFileSync, rmSync, existsSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const raiz = join(dirname(fileURLToPath(import.meta.url)), '..', '..');

const args = process.argv.slice(2);
const opcao = (nome, padrao) => {
  const i = args.indexOf(`--${nome}`);
  return i >= 0 && args[i + 1] ? args[i + 1] : padrao;
};

const BASE = opcao('url', 'http://localhost:5123');
const SAIDA = join(raiz, opcao('saida', 'ferramentas/bruno/nexora'));

// ===================== O ID DE CADA ROTA VIRA VARIAVEL =====================
// `/api/contatos/{id}` sem isto viraria `:id` com valor 1 fixo, e a colecao so serviria para
// olhar. Mapeado por TAG, o mesmo `{id}` vira `{{contatoId}}` em Contatos e `{{canalId}}` em
// Canais — e o fluxo "lista, copia um id, usa nas outras" passa a funcionar.
// ==========================================================================
const VAR_POR_TAG = {
  Contatos: 'contatoId', Conversas: 'conversaId', Conexoes: 'conexaoId',
  Canais: 'canalId', Etapas: 'etapaId', Equipe: 'usuarioId', Feriados: 'feriadoId',
  Lembretes: 'lembreteId', Formularios: 'formularioId', Funil: 'contatoId',
  Midia: 'mensagemId', WebhooksSaida: 'entregaId'
};

// Nomes de parametro que COLIDIRIAM com variavel do ambiente. `{token}` do convite nao e o JWT
// — resolver os dois para `{{token}}` mandaria o token de sessao no lugar do de convite.
const RENOMEIA = { token: 'tokenConvite', chave: 'chaveFormulario' };

// ===================== O CABECALHO QUE O DOCUMENTO NAO DECLARA =====================
// `X-Chave-Admin` e lido de `Request.Headers` dentro da acao, nao e parametro declarado — entao o
// Swashbuckle nao o poe no documento, e o gerador, que so le o documento, nao teria como saber
// que ele existe. Sem esta lista as duas rotas nasceriam sem a chave e voltariam
// `401 {"erro":"Nao autorizado."}` — a MESMA resposta de chave errada, deliberadamente sem pista
// (CadastroController.cs:52). Quem abrisse a colecao iria procurar a chave certa quando o que
// faltava era o header.
//
// Isto e remendo, e o remendo tem custo: some se alguem mudar a rota de lugar. O conserto de raiz
// e a acao DECLARAR a exigencia (atributo lido tanto pelo pipeline quanto por um filtro de
// Swagger), do mesmo jeito que `[Authorize]` ja e — ai o documento diz a verdade e este mapa
// deixa de existir.
const CABECALHO_EXTRA = {
  '/api/cadastro/empresa': { 'X-Chave-Admin': '{{chaveAdministracao}}' },
  '/api/demonstracao/semear': { 'X-Chave-Admin': '{{chaveAdministracao}}' }
};

// A ordem das pastas na barra lateral. Auth primeiro porque e onde se comeca: sem rodar o login,
// nada mais funciona.
const ORDEM = [
  'Auth', 'Painel', 'Caixa', 'Conversas', 'Contatos', 'Funil', 'MeuDia', 'Lembretes',
  'Dashboard', 'Conexoes', 'Canais', 'Formularios', 'WebhooksSaida', 'Etapas', 'Equipe',
  'Configuracao', 'Feriados', 'Conta', 'Onboarding', 'Midia',
  'Captura', 'Webhook', 'Cadastro', 'Convite', 'Redefinicao', 'Demonstracao', 'Dev'
];

// ==================================================================== exemplos de corpo
/** Um JSON de exemplo a partir do schema. Sem isto a requisicao nasce com corpo vazio e quem
 *  abre a colecao tem que ler o Swagger em paralelo — que e exatamente o trabalho que a colecao
 *  deveria poupar. */
function exemplo(schema, docs, profundidade = 0) {
  if (!schema || profundidade > 6) return null;

  if (schema.$ref) {
    const nome = schema.$ref.split('/').pop();
    return exemplo(docs.components?.schemas?.[nome], docs, profundidade + 1);
  }

  // `allOf` aparece quando o Swashbuckle envolve um enum ou um tipo derivado.
  if (schema.allOf?.length) return exemplo(schema.allOf[0], docs, profundidade + 1);
  if (schema.enum?.length) return schema.enum[0];

  switch (schema.type) {
    case 'object': {
      const objeto = {};
      for (const [campo, sub] of Object.entries(schema.properties ?? {})) {
        objeto[campo] = valorDoCampo(campo, sub, docs, profundidade);
      }
      return objeto;
    }
    case 'array':
      return [exemplo(schema.items, docs, profundidade + 1)].filter(x => x !== null);
    case 'integer': return 0;
    case 'number': return 0;
    case 'boolean': return false;
    case 'string': return textoDe(schema);
    default: return schema.properties ? exemplo({ ...schema, type: 'object' }, docs, profundidade) : null;
  }
}

/** Campos com nome conhecido saem ligados ao AMBIENTE. E o que faz o login rodar no primeiro
 *  clique, em vez de exigir que alguem digite e-mail e senha dentro do corpo. */
function valorDoCampo(campo, schema, docs, profundidade) {
  const ligado = { email: '{{email}}', senha: '{{senha}}' };
  if (ligado[campo] !== undefined && (schema.type === 'string' || schema.$ref === undefined)) {
    return ligado[campo];
  }
  return exemplo(schema, docs, profundidade + 1);
}

function textoDe(schema) {
  switch (schema.format) {
    case 'date-time': return '2026-08-06T12:00:00Z';
    case 'date': return '2026-08-06';
    case 'uuid': return '00000000-0000-0000-0000-000000000000';
    case 'email': return '{{email}}';
    default: return '';
  }
}

// ==================================================================== .bru
const bloco = (cabeca, corpo) => `${cabeca} {\n${corpo}\n}\n`;
const linhas = (obj, prefixo = '') =>
  Object.entries(obj).map(([k, v]) => `  ${prefixo}${k}: ${v}`).join('\n');

function requisicao({ metodo, caminho, tag, seq, docs, operacao }) {
  const publica = !operacao.security || operacao.security.length === 0;

  const params = operacao.parameters ?? [];
  const path = params.filter(p => p.in === 'path');
  const query = params.filter(p => p.in === 'query');

  // Bruno usa `:nome` no caminho e resolve pelo bloco `params:path`.
  let url = `{{baseUrl}}${caminho.replace(/\{(\w+)\}/g, ':$1')}`;

  const corpoSchema = operacao.requestBody?.content?.['application/json']?.schema;
  const temCorpo = !!corpoSchema;

  let bru = bloco('meta', linhas({
    name: `${metodo.toUpperCase()} ${caminho.replace('/api/', '/')}`,
    type: 'http',
    seq
  }));

  bru += '\n' + bloco(metodo, linhas({
    url,
    body: temCorpo ? 'json' : 'none',
    // `inherit` puxa o bearer da colecao; `none` e o que impede mandar Authorization para quem
    // nao pede — e a razao de o documento OpenAPI precisar estar certo (ver
    // FiltroSegurancaSwagger).
    auth: publica ? 'none' : 'inherit'
  }));

  if (path.length) {
    const valores = {};
    for (const p of path) {
      const nome = RENOMEIA[p.name] ?? (p.name === 'id' ? VAR_POR_TAG[tag] ?? 'id' : p.name);
      valores[p.name] = `{{${nome}}}`;
    }
    bru += '\n' + bloco('params:path', linhas(valores));
  }

  if (query.length) {
    // Opcional entra DESLIGADO (`~`): uma colecao que manda todo filtro preenchido devolve lista
    // vazia no primeiro clique, e quem abriu conclui que a API esta quebrada.
    const valores = {};
    for (const p of query) {
      const chave = p.required ? p.name : `~${p.name}`;
      valores[chave] = exemploDeParametro(p, docs);
    }
    bru += '\n' + bloco('params:query', linhas(valores));
  }

  const cabecalhos = {
    ...(temCorpo ? { 'Content-Type': 'application/json' } : {}),
    ...(CABECALHO_EXTRA[caminho] ?? {})
  };
  if (Object.keys(cabecalhos).length) bru += '\n' + bloco('headers', linhas(cabecalhos));

  if (temCorpo) {
    const json = JSON.stringify(exemplo(corpoSchema, docs) ?? {}, null, 2)
      .split('\n').map(l => '  ' + l).join('\n');
    bru += '\n' + `body:json {\n${json}\n}\n`;
  }

  // O login guarda o token no ambiente. E o unico script da colecao, e o que dispensa copiar e
  // colar JWT a cada expiracao.
  if (caminho === '/api/auth/login') {
    bru += '\n' + bloco('script:post-response', [
      '  const corpo = res.getBody();',
      '  if (res.getStatus() === 200 && corpo && corpo.token) {',
      '    bru.setEnvVar("token", corpo.token);',
      '    console.log("token guardado para " + corpo.usuario.email);',
      '  }'
    ].join('\n'));

    bru += '\n' + bloco('tests', [
      '  test("login devolve token", function () {',
      '    expect(res.getStatus()).to.equal(200);',
      '    expect(res.getBody().token).to.be.a("string");',
      '  });'
    ].join('\n'));
  }

  return bru;
}

function exemploDeParametro(p, docs) {
  const s = p.schema ?? {};
  if (s.$ref || s.allOf) {
    const v = exemplo(s, docs);
    return v === null || v === '' ? '' : v;
  }
  if (s.default !== undefined) return s.default;
  if (s.enum?.length) return s.enum[0];
  if (s.type === 'integer' || s.type === 'number') return p.name.toLowerCase().includes('tamanho') ? 20 : 1;
  if (s.type === 'boolean') return false;
  return '';
}

// ==================================================================== geracao
const slug = t => t.toLowerCase()
  .normalize('NFD').replace(/[̀-ͯ]/g, '')
  .replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '') || 'req';

const doc = await (await fetch(`${BASE}/swagger/v1/swagger.json`)).json();

// O mapa de cabecalho extra e escrito a mao, logo apodrece calado se a rota mudar de lugar: a
// chave deixa de casar, o header some, e a requisicao volta a dar 401 sem explicacao. Falhar aqui
// e o que transforma isso em erro visivel na hora de regerar.
const orfas = Object.keys(CABECALHO_EXTRA).filter(r => !doc.paths[r]);
if (orfas.length) {
  console.error(`ERRO: CABECALHO_EXTRA aponta para rota que nao existe mais: ${orfas.join(', ')}`);
  console.error('Ajuste o mapa em gerar.mjs antes de usar a colecao.');
  process.exit(1);
}

// Apaga e recria: colecao gerada e ARTEFATO, e sobra de execucao anterior (rota que deixou de
// existir) e a forma mais silenciosa de a colecao mentir.
if (existsSync(SAIDA)) rmSync(SAIDA, { recursive: true, force: true });
mkdirSync(join(SAIDA, 'environments'), { recursive: true });

writeFileSync(join(SAIDA, 'bruno.json'), JSON.stringify({
  version: '1',
  name: 'Nexora',
  type: 'collection',
  ignore: ['node_modules', '.git']
}, null, 2) + '\n');

// Bearer na COLECAO, herdado por todas as requisicoes protegidas. Declarar por requisicao daria
// 87 copias do mesmo `{{token}}`.
writeFileSync(join(SAIDA, 'collection.bru'),
  bloco('auth', linhas({ mode: 'bearer' })) + '\n' +
  bloco('auth:bearer', linhas({ token: '{{token}}' })) + '\n' +
  bloco('headers', linhas({ Accept: 'application/json' })));

const porTag = new Map();
for (const [caminho, metodos] of Object.entries(doc.paths)) {
  for (const [metodo, operacao] of Object.entries(metodos)) {
    const tag = operacao.tags?.[0] ?? 'Outros';
    if (!porTag.has(tag)) porTag.set(tag, []);
    porTag.get(tag).push({ caminho, metodo, operacao });
  }
}

let pastas = 0, reqs = 0, publicas = 0;

for (const [tag, ops] of porTag) {
  const pasta = join(SAIDA, tag);
  mkdirSync(pasta, { recursive: true });
  pastas++;

  const ordem = ORDEM.indexOf(tag);
  writeFileSync(join(pasta, 'folder.bru'),
    bloco('meta', linhas({ name: tag, seq: ordem >= 0 ? ordem + 1 : 90 })));

  // Dentro da pasta: GET primeiro (ler antes de escrever), depois pelo caminho.
  const peso = { get: 0, post: 1, put: 2, patch: 3, delete: 4 };
  ops.sort((a, b) => (peso[a.metodo] ?? 9) - (peso[b.metodo] ?? 9)
    || a.caminho.localeCompare(b.caminho));

  ops.forEach(({ caminho, metodo, operacao }, i) => {
    const bru = requisicao({ metodo, caminho, tag, seq: i + 1, docs: doc, operacao });
    const nome = slug(`${metodo}-${caminho.replace('/api/', '')}`);
    writeFileSync(join(pasta, `${nome}.bru`), bru);
    reqs++;
    if (!operacao.security || operacao.security.length === 0) publicas++;
  });
}

// ==================================================================== ambiente
// Segredo NAO entra no arquivo: `vars:secret` faz o Bruno guardar fora do .bru, e e o que
// permite versionar a colecao sem versionar credencial. O `token` e secreto pelo mesmo motivo —
// ele e reescrito a cada login e nao deveria virar diff no git.
const ambiente =
  bloco('vars', linhas({
    baseUrl: BASE,
    email: 'ana@padaria.com',
    contatoId: 1,
    conversaId: 1,
    conexaoId: 1,
    canalId: 1,
    etapaId: 1,
    usuarioId: 1,
    feriadoId: 1,
    lembreteId: 1,
    formularioId: 1,
    mensagemId: 1,
    entregaId: 1,
    tokenConvite: 'cole-o-token-do-convite-aqui',
    chaveFormulario: 'cole-a-chave-do-formulario-aqui'
  })) + '\n' +
  // `vars:secret` usa COLCHETE e lista separada por virgula — nao e um bloco `chave: valor` como
  // os outros. Com chave, o Bruno le o arquivo mas ignora a secao, e a senha vira variavel comum:
  // gravada em texto no .bru e versionada junto.
  `vars:secret [\n  ${['senha', 'token', 'segredoWebhook', 'chaveAdministracao'].join(',\n  ')}\n]\n`;

writeFileSync(join(SAIDA, 'environments', 'local.bru'), ambiente);

console.log(`Colecao gerada em ${SAIDA}`);
console.log(`  ${pastas} pastas, ${reqs} requisicoes (${publicas} publicas, ${reqs - publicas} com bearer)`);
console.log(`  ambiente: environments/local.bru  (senha e token sao secretos, preencha no Bruno)`);
