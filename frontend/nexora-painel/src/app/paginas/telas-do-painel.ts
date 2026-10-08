import { Type, signal } from '@angular/core';
import { convertToParamMap } from '@angular/router';
import { Subject, of } from 'rxjs';

import { Shell } from '../layout/shell/shell';
import { Caixa } from './caixa/caixa';
import { Canais } from './canais/canais';
import { Captacao } from './captacao/captacao';
import { Comecar } from './comecar/comecar';
import { Conexao } from './conexao/conexao';
import { Configuracoes } from './configuracoes/configuracoes';
import { Conta } from './conta/conta';
import { Contato } from './contato/contato';
import { Contatos } from './contatos/contatos';
import { Importar } from './importar/importar';
import { LeadsParados } from './leads-parados/leads-parados';
import { Convite } from './convite/convite';
import { Dashboard } from './dashboard/dashboard';
import { Equipe } from './equipe/equipe';
import { Esqueci } from './esqueci/esqueci';
import { Etapas } from './etapas/etapas';
import { Etiquetas } from './etiquetas/etiquetas';
import { Evolucao } from './evolucao/evolucao';
import { Pipelines } from './pipelines/pipelines';
import { Formularios } from './formularios/formularios';
import { Funil } from './funil/funil';
import { Integracoes } from './integracoes/integracoes';
import { IntegracaoAnuncios } from './integracoes/anuncios/anuncios';
import { IntegracaoWebhook } from './integracoes/webhook/webhook';
import { Login } from './login/login';
import { Mais } from './mais/mais';
import { MeuDia } from './meu-dia/meu-dia';
import { Redefinir } from './redefinir/redefinir';
import { CriarEmpresa } from './criar-empresa/criar-empresa';
import { Operacao } from './operacao/operacao';
import { OperacaoEmpresas } from './operacao/empresas/empresas';
import { OperacaoPlanos } from './operacao/planos/planos';

/** ===================== O INVENTÁRIO DE TELAS, NUM LUGAR SÓ (MOB-2) =====================
 *  Duas suítes montam TODAS as telas do painel e precisam da mesma lista:
 *
 *    paginas.render.spec.ts    desktop  — cada tela monta e desenha sem estourar
 *    paginas.celular.spec.ts   390px    — nenhuma tela transborda na largura
 *
 *  Com a lista escrita duas vezes, uma tela nova entra numa e não na outra — e o arquivo que
 *  ficou para trás continua verde, dando a impressão de cobrir tudo. É a mesma razão pela qual
 *  as primitivas de `styles.css` foram consolidadas: cópia não diverge de uma vez, diverge aos
 *  poucos e em silêncio.
 *
 *  ⚠️ ESTE ARQUIVO NÃO É `.spec.ts`, e é de propósito: `tsconfig.app.json` inclui `src/**\/*.ts`
 *  e exclui só os specs, então nada aqui pode depender de jasmine nem de `@angular/core/testing`.
 *  Ele guarda DADO — a lista e as respostas falsas —, e a mecânica de montar fica em cada suíte.
 *  ==================================================================================== */

/** Corpo único para toda resposta pendente. É um SUPERSET das formas que as telas esperam —
 *  campo a mais o JavaScript ignora, e o que importa é nenhuma lista chegar `undefined`, que
 *  é o que faria um `@for` estourar por culpa do teste e não do código. */
export const CORPO = {
  itens: [], temMais: false, total: 0, numeroPagina: 1, tamanho: 30,
  // O histórico de compras da ficha e da Caixa (AUD-XX): a lista e o resumo, nulo = nunca comprou.
  vendas: [], resumo: null,
  colunas: [], etapas: [], passos: [], acoes: [], usuarios: [], feriados: [],
  conversas: [], contatos: [], lembretes: [], series: [], atividades: [], conexoes: [],
  funil: [], origens: [], pontos: [], concluidos: 0, webhook: null,
  // Os tetos da empresa e o uso das conexões (AUD-XX).
  limitePipelines: { emUso: 1, limite: 4, cheio: false },
  limiteEtiquetas: { emUso: 0, limite: 60, cheio: false },
  limiteEtapasDoFunil: { emUso: 0, limite: 12, cheio: false },
  etiquetasPorNegocio: 8, emUso: 0,
  // O resumo da Captação (AUD-XX).
  leadsTotal: 0, leadsCanais: 0, leadsFormularios: 0, percentualCanais: null, percentualFormularios: null,
  canaisAtivos: 0, totalCanais: 0, formulariosAtivos: 0, totalFormularios: 0, leadsDeAnuncioSemEnvio: 0,
  // O registro do webhook é uma página com total (AUD-XX).
  entregas: { itens: [], totalCount: 0, pagina: 1, tamanhoPagina: 20, totalPaginas: 1 }, falhas: 0,
  // AUD-XX: `contagens` é OBJETO, e duas telas o leem com chaves diferentes — Contatos (`abertos`,
  // `ganhos`, `perdidos`, `todos`) e o Meu Dia paginado (`todas`, `responder`, `lembrete`,
  // `atrasadas`). O superset leva as oito; faltando, o Meu Dia estoura em `contagens.todas`.
  contagens: {
    abertos: 0, ganhos: 0, perdidos: 0, todos: 0,
    todas: 0, responder: 0, lembrete: 0, atrasadas: 0
  },
  totalCount: 0, totalPaginas: 1,
  // EVO-1: `pessoas` e `meses` são listas; `equipe` é OBJETO OU NULO, e nulo é o caso de quem
  // não tem `ver_numeros_da_equipe` — a tela já tem de desenhar sem régua.
  pessoas: [], meses: [], equipe: null,
  // LPA-1: `/relatorios/opcoes` passou a ser lido por uma tela que ESTA no laco. Sem estas
  // tres chaves, `opcoes().responsaveis.length` estoura com "undefined" por culpa do teste.
  responsaveis: [], motivosPerda: [],
  // A aba de Anúncios: `conversoes` é lista e `leadsComAnuncio30Dias` vira número na tela.
  // ⚠️ `vendasSemEnvio` é OBJETO, não lista (INT-5): a tela lê `.total` dele direto, e omiti-lo
  // derruba toda suíte que monta qualquer tela do painel com "Cannot read properties of undefined".
  conversoes: [], credencial: null, leadsComAnuncio30Dias: 0,
  vendasSemEnvio: { total: 0, valorTotal: 0, diasDaJanela: 21, vendas: [] },
  mostrar: false, completo: false, dispensado: false,
  // A área do operador: a lista devolve envelope de página (já coberto por `itens`/`total`
  // acima) e o catálogo devolve ARRAY — este último está em `RESPONDEM_ARRAY`.
  ativa: true, demonstracao: false, planoId: null, planoNome: null,
  limiteConexoes: 1, limiteUsuarios: 3, conexoesUsadas: 0, vagasUsadas: 1,
  naoLidas: 0, whatsappConectado: true, trocouDeNumero: false,
  janelaHoraInicio: 8, janelaHoraFim: 20, janelaDiasSemana: 126, feriadosRecentes: [],
  status: 'nao_criada', nome: '', email: '', telefone: '', papel: 'dono',
  // O detalhe do contato lê `dados().contato`; sem isto a tela desenha vazia por culpa do
  // teste, não do código.
  contato: {
    id: 1, nome: 'Cliente', telefone: '5584900000000', email: null, origem: 'manual',
    responsavelId: null, valor: null, etapaId: 1, etapaNome: 'Novo Lead',
    negocios: [{
      id: 10, pipelineId: 1, pipelineNome: 'Vendas',
      etapaId: 1, etapaNome: 'Novo Lead', status: 'aberta', valor: null
    }]
  }
};

/** Endpoints que respondem ARRAY, não objeto. Mandar `CORPO` neles faz o `@for` estourar com
 *  "not iterable" — e o erro seria do teste, não da tela. Lista explícita porque a URL sozinha
 *  não diz a forma da resposta. */
export const RESPONDEM_ARRAY = [
  '/equipe', '/feriados', '/lembretes/contato/',
  // `/configuracao/` como PREFIXO: cobre fusos, ufs e qualquer lista nova sob ele. Era assim
  // numa das copias locais que esta constante absorveu, e a forma mais larga e a que nao deixa
  // suite nenhuma para tras.
  '/configuracao/', '/formularios', '/etapas', '/etiquetas',
  // `/contatos/{id}/vendas` e `/trilha/` SAÍRAM daqui (AUD-XX): o primeiro responde
  // `{ vendas, resumo }`, o segundo uma página com total — os dois saem de `CORPO`.
  '/pipelines',
  // OPE-1: o catálogo de planos responde array; a lista de empresas responde envelope.
  '/operador/planos',
  // NPS-1: o histórico de notas da ficha é lista. Com `CORPO`, `notas().length` dava `undefined` e
  // o bloco só não aparecia por acaso.
  '/pesquisas-nps/contato/'
];

export const TELAS: { nome: string; componente: Type<unknown> }[] = [
  { nome: 'Shell (layout)', componente: Shell },
  { nome: 'Login', componente: Login },
  { nome: 'Esqueci minha senha', componente: Esqueci },
  { nome: 'Convite', componente: Convite },
  { nome: 'Redefinir senha', componente: Redefinir },

  // A tela do OPERADOR, não do cliente. Entra aqui como as outras públicas — e, diferente delas,
  // não pede NADA ao montar: nenhuma chave nova em `CORPO`, nenhuma URL nova em `RESPONDEM_ARRAY`.
  // Dito aqui para ninguém ir procurar.
  //
  // Registrar nesta lista é o que lhe dá o teste de 390px, e esse é o ÚNICO guarda visual
  // automático que ela tem: tela pública não entra no `design-system.spec.ts` nem no
  // `larguras.spec.ts`, que medem `.pagina` — algo que páginas públicas não têm.
  { nome: 'Criar empresa (operador)', componente: CriarEmpresa },

  // A área do operador. O container entra TRAVADO (sem chave, ele só desenha o pedido dela), e é
  // nesse estado que a suíte de render o monta — o que já cobre a tela que o operador vê primeiro.
  { nome: 'Operação (operador)', componente: Operacao },

  // ⚠️ OS DOIS PAINÉIS, pelo mesmo motivo dos de Captação e Integrações: o container renderiza só
  // a aba ATIVA, então sem estas entradas o painel de planos nunca seria montado — e trocar a aba
  // padrão um dia tiraria cobertura sem ninguém perceber.
  { nome: 'Operação — painel de empresas', componente: OperacaoEmpresas },
  { nome: 'Operação — painel de planos', componente: OperacaoPlanos },
  { nome: 'Primeiros passos', componente: Comecar },
  { nome: 'Caixa de entrada', componente: Caixa },
  { nome: 'Dashboard', componente: Dashboard },
  { nome: 'Meu Dia', componente: MeuDia },
  { nome: 'Funil', componente: Funil },
  { nome: 'Contatos', componente: Contatos },
  { nome: 'Detalhe do contato', componente: Contato },
  { nome: 'Importar leads', componente: Importar },
  { nome: 'Equipe', componente: Equipe },
  { nome: 'Conexão', componente: Conexao },
  { nome: 'Configurações', componente: Configuracoes },
  { nome: 'Etapas do funil', componente: Etapas },
  { nome: 'Etiquetas', componente: Etiquetas },
  { nome: 'Pipelines', componente: Pipelines },
  { nome: 'Captação', componente: Captacao },
  { nome: 'Integrações', componente: Integracoes },
  { nome: 'Conta', componente: Conta },
  { nome: 'Mais (menu do celular)', componente: Mais },

  // ===== PAINÉIS, não rotas (NAV-1) =====
  // Estes dois perderam a rota própria e viraram abas de Captação. Continuam na lista porque
  // continuam sendo montados sozinhos — e porque a aba de QR só é exercitada aqui: dentro de
  // Captação, quem renderiza é a aba ATIVA, e ela nasce em Formulários.
  { nome: 'Captação — painel de formulários', componente: Formularios },
  { nome: 'Captação — painel de QR e links', componente: Canais },

  // ⚠️ OS DOIS PAINÉIS DE INTEGRAÇÕES, pelo mesmo motivo dos de Captação — e esta entrada nasceu
  // de um vermelho real. `Integracoes` renderiza só a aba ATIVA, e no dia em que a aba padrão
  // passou de Webhook para Anúncios o painel de webhook deixou de ser montado aqui, sem ninguém
  // perceber. Listados os dois, trocar o padrão de novo não tira cobertura de nada.
  { nome: 'Integrações — painel de anúncios', componente: IntegracaoAnuncios },
  { nome: 'Integrações — painel de webhook', componente: IntegracaoWebhook },

  // ⚠️ EVOLUÇÃO ENTRA AQUI, E RELATÓRIOS NÃO — e a diferença não é descuido. Relatórios ficou de
  // fora porque COM DADOS ela transborda ~150px em 390px (defeito anterior, anotado). Esta tela
  // nasceu com a tabela dentro de `.tabela-rolagem`, então a rolagem é do container e não da
  // página — o laço de celular é justamente quem guarda isso.
  { nome: 'Evolução', componente: Evolucao },

  // LPA-1. O corpo falso ja traz `itens` e `total`, que e tudo que esta tela le.
  { nome: 'Leads parados', componente: LeadsParados }
];

/** Sem SignalR no teste: abrir socket ali só traria intermitência. */
export class RealtimeFalso {
  conectado = signal(true);
  mensagemRecebida$ = new Subject<never>();
  conversaAberta$ = new Subject<never>();
  contatoCriado$ = new Subject<never>();
  statusMensagem$ = new Subject<never>();
  conexaoMudou$ = new Subject<never>();
  async conectar() { }
  desconectar() { }
}

/** ===================== A LARGURA-ALVO DO CELULAR (MOB-2) =====================
 *  390px é o iPhone 12/13/14 e a faixa em que quase todo Android cai — o alvo que a auditoria de
 *  `docs/MOBILE.md` usou, e a largura em que as suítes de celular medem transbordo.
 *
 *  ⚠️ NÃO é a largura da JANELA do teste. O Chrome headless trava a janela em ~504px e ignora
 *  qualquer pedido menor (ver karma.conf.js). A janela serve para as media queries do produto
 *  ficarem ativas; a medição acontece numa caixa desta largura. As duas coisas juntas é que dão
 *  a resposta certa — janela de celular monta o layout de celular, e a caixa o mede no tamanho
 *  que importa.
 *  ============================================================================= */
export const LARGURA_CELULAR = 390;


/** ===================== O `ActivatedRoute` FALSO, COMPLETO (MOB-2) =====================
 *  As suítes montavam as telas com um `ActivatedRoute` que só tinha `snapshot`. Bastava enquanto
 *  ninguém observava a rota — e deixou de bastar quando a caixa de entrada passou a guardar a
 *  conversa aberta em `?conversa=`: ler o `snapshot` uma vez faria o Voltar do navegador mudar o
 *  endereço sem mudar a tela.
 *
 *  ⚠️ SERVE PARA MONTAR, NÃO PARA NAVEGAR. Quem clica e espera a URL mudar precisa de roteador de
 *  verdade — `RouterTestingHarness`, como em `caixa.spec.ts`. Aqui os observáveis emitem o valor
 *  inicial e pronto, que é o que uma tela recém-montada consome.
 *  ====================================================================================== */
export function rotaFalsa(
  // ⚠️ `pipeline` ENTROU NOS PADRÕES porque `crm/:pipeline/etapas` existe: sem ele, a tela de
  // etapas montava no estado "Funil não encontrado" e as suítes genéricas passavam medindo uma
  // tela de erro. Um padrão que não cobre as rotas reais é um teste que confere o vazio.
  params: Record<string, string> = { token: 'token-de-teste', id: '1', pipeline: '1' },
  query: Record<string, string> = {}
) {
  const p = convertToParamMap(params);
  const q = convertToParamMap(query);
  return {
    snapshot: { paramMap: p, queryParamMap: q, data: {} },
    paramMap: of(p),
    queryParamMap: of(q),
    params: of(params),
    queryParams: of(query),
    data: of({})
  };
}
