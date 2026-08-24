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
import { Convite } from './convite/convite';
import { Dashboard } from './dashboard/dashboard';
import { Equipe } from './equipe/equipe';
import { Esqueci } from './esqueci/esqueci';
import { Etapas } from './etapas/etapas';
import { Formularios } from './formularios/formularios';
import { Funil } from './funil/funil';
import { Integracoes } from './integracoes/integracoes';
import { Login } from './login/login';
import { Mais } from './mais/mais';
import { MeuDia } from './meu-dia/meu-dia';
import { Redefinir } from './redefinir/redefinir';

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
  colunas: [], etapas: [], passos: [], acoes: [], usuarios: [], feriados: [],
  conversas: [], contatos: [], lembretes: [], series: [], atividades: [], conexoes: [],
  funil: [], origens: [], pontos: [], concluidos: 0, entregas: [], webhook: null,
  mostrar: false, completo: false, dispensado: false,
  naoLidas: 0, whatsappConectado: true, trocouDeNumero: false,
  janelaHoraInicio: 8, janelaHoraFim: 20, janelaDiasSemana: 126, feriadosRecentes: [],
  status: 'nao_criada', nome: '', email: '', telefone: '', papel: 'dono',
  // O detalhe do contato lê `dados().contato`; sem isto a tela desenha vazia por culpa do
  // teste, não do código.
  contato: {
    id: 1, nome: 'Cliente', telefone: '5584900000000', email: null, origem: 'manual',
    responsavelId: null, valor: null, etapaId: 1, etapaNome: 'Novo Lead'
  }
};

/** Endpoints que respondem ARRAY, não objeto. Mandar `CORPO` neles faz o `@for` estourar com
 *  "not iterable" — e o erro seria do teste, não da tela. Lista explícita porque a URL sozinha
 *  não diz a forma da resposta. */
export const RESPONDEM_ARRAY = [
  '/equipe', '/feriados', '/lembretes/contato/',
  '/configuracao/fusos', '/configuracao/ufs', '/formularios', '/etapas',
  '/vendas', '/trilha/'
];

export const TELAS: { nome: string; componente: Type<unknown> }[] = [
  { nome: 'Shell (layout)', componente: Shell },
  { nome: 'Login', componente: Login },
  { nome: 'Esqueci minha senha', componente: Esqueci },
  { nome: 'Convite', componente: Convite },
  { nome: 'Redefinir senha', componente: Redefinir },
  { nome: 'Primeiros passos', componente: Comecar },
  { nome: 'Caixa de entrada', componente: Caixa },
  { nome: 'Dashboard', componente: Dashboard },
  { nome: 'Meu Dia', componente: MeuDia },
  { nome: 'Funil', componente: Funil },
  { nome: 'Contatos', componente: Contatos },
  { nome: 'Detalhe do contato', componente: Contato },
  { nome: 'Equipe', componente: Equipe },
  { nome: 'Conexão', componente: Conexao },
  { nome: 'Configurações', componente: Configuracoes },
  { nome: 'Etapas do funil', componente: Etapas },
  { nome: 'Captação', componente: Captacao },
  { nome: 'Integrações', componente: Integracoes },
  { nome: 'Conta', componente: Conta },
  { nome: 'Mais (menu do celular)', componente: Mais },

  // ===== PAINÉIS, não rotas (NAV-1) =====
  // Estes dois perderam a rota própria e viraram abas de Captação. Continuam na lista porque
  // continuam sendo montados sozinhos — e porque a aba de QR só é exercitada aqui: dentro de
  // Captação, quem renderiza é a aba ATIVA, e ela nasce em Formulários.
  { nome: 'Captação — painel de formulários', componente: Formularios },
  { nome: 'Captação — painel de QR e links', componente: Canais }
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
  params: Record<string, string> = { token: 'token-de-teste', id: '1' },
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
