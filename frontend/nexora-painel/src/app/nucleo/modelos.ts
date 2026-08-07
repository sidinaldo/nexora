/** Contratos que a API devolve. Escritos a partir do que os blocos 1–4 expõem — o
 *  `modelos.ts` do Recupera tem 941 linhas de DTO de cobrança e não serve.
 *
 *  Enum sempre como TEXTO (a API serializa com JsonStringEnumConverter): assim a ordem
 *  do enum em C# não vira uma duplicata implícita aqui, que quebraria em silêncio se
 *  alguém inserisse um valor no meio. */

export type PapelUsuario = 'dono' | 'gestor' | 'vendedor';
export type StatusUsuario = 'ativo' | 'convidado' | 'inativo';
export type DirecaoMensagem = 'entrada' | 'saida';
export type StatusConversa = 'aberta' | 'resolvida';
export type TipoMidia = 'nenhum' | 'imagem' | 'documento' | 'audio' | 'video';
export type StatusConexao = 'nao_criada' | 'conectando' | 'conectado' | 'desconectado' | 'offline';

export type FiltroConversa = 'Aguardando' | 'Minhas' | 'NaoAtribuidas' | 'Todas' | 'Resolvidas';

// ---------------------------------------------------------------- auth
export interface UsuarioAutenticado {
  id: number;
  nome: string;
  email: string;
  papel: PapelUsuario;
  empresaId: number;
  empresaNome: string;
}

export interface LoginResponse {
  token: string;
  expiraEm: string;
  usuario: UsuarioAutenticado;
}

/** Formato de erro da API: o FiltroRegraDeNegocio devolve sempre { erro: "..." },
 *  com mensagem já em português e voltada ao usuário final. */
export interface ErroApi {
  erro: string;
}

// ---------------------------------------------------------------- paginação
/** Página por CURSOR (não por offset). A lista da caixa se reordena em tempo real —
 *  com offset, a página seguinte pula ou repete linha. */
export interface PaginaCursor<T> {
  itens: T[];
  temMais: boolean;
}

/** Página por OFFSET, com total. Usada onde a lista NÃO se reordena sozinha (contatos, que é
 *  ordenada por nome) — e onde o total importa para mostrar "142 contatos". */
export interface Pagina<T> {
  total: number;
  numeroPagina: number;
  tamanho: number;
  itens: T[];
}

// ---------------------------------------------------------------- contatos e funil
export type OrigemLead =
  | 'instagram' | 'facebook' | 'whatsapp' | 'google'
  | 'site' | 'qrcode' | 'indicacao' | 'manual' | 'outro';

export type FiltroContato = 'Abertos' | 'Ganhos' | 'Perdidos' | 'Todos';

export interface ContatoResumo {
  id: number;
  nome: string;
  telefone: string;
  email: string | null;
  origem: OrigemLead;
  etapaId: number;
  etapaNome: string;
  ordemKanban: number;
  responsavelId: number | null;
  responsavelNome: string | null;
  valor: number | null;
  ganhoEm: string | null;
  perdidoEm: string | null;
  criadoEm: string;
  conversaId: number | null;
  aguardandoDesde: string | null;
  naoLidas: number;
}

/** O card do kanban. Projeção mais enxuta que a da lista: o quadro carrega dezenas por coluna,
 *  e cada campo a mais é multiplicado pelo número de cards na tela. */
export interface ContatoCard {
  id: number;
  nome: string;
  telefone: string;
  ordemKanban: number;
  valor: number | null;
  /** Quantas vendas EM ABERTO este contato tem (NEG-2). O quadro é montado por CONTATO, e quem
   *  comprou três vezes aparece num card só — o número resolve o que a tela precisa mostrar. */
  vendasEmAberto: number;
  responsavelId: number | null;
  responsavelNome: string | null;
  conversaId: number | null;
  aguardandoDesde: string | null;
  naoLidas: number;
  ultimaMensagemEm: string | null;
  /** `xmin` da linha. Volta ao servidor no arrasto; se outra pessoa mexeu no card no meio do
   *  caminho, a API recusa com 409 e a coluna é recarregada. */
  versao: number;
}

export interface ContatoDetalhe {
  contato: ContatoResumo;
  origemDetalhe: string | null;
  observacoes: string | null;
  motivoPerda: string | null;
  anonimizadoEm: string | null;
  ultimaMensagemEm: string | null;
  lembretes: LembreteDto[];
}

export interface ColunaFunil {
  etapaId: number;
  nome: string;
  ordem: number;
  cor: string;
  eGanho: boolean;
  /** Do conjunto INTEIRO da coluna, não da página carregada. */
  total: number;
  valorTotal: number;
  /** Quantas vendas já foram CONCLUÍDAS nesta etapa (NEG-2). Zero fora da etapa de ganho.
   *  Sem esse segundo número, a coluna esvaziando pareceria perda de dado. */
  concluidas: number;
  contatos: ContatoCard[];
  temMais: boolean;
}

export interface QuadroFunil {
  colunas: ColunaFunil[];
}

// ---------------------------------------------------------------- painel
export interface StatusPainel {
  naoLidas: number;
  aguardando: number;
  /** false quando ALGUMA conexão já pareada está fora do ar — não quando todas estão.
   *  Com dois números, esperar os dois caírem significa deixar o vendedor digitar resposta
   *  num número morto enquanto o painel diz que está tudo bem. */
  whatsappConectado: boolean;
  /** Os NOMES das conexões caídas, para o banner dizer qual. Vazio quando está tudo no ar. */
  conexoesCaidas: string[];
  trocouDeNumero: boolean;
  /** Limites do semáforo, em minutos. Vêm do servidor, mas quem PINTA é o cliente:
   *  a cor envelhece entre requisições e a lista precisa amadurecer sozinha. */
  semaforoAmareloMinutos: number;
  semaforoVermelhoMinutos: number;
  /** A janela de atendimento da empresa. Vem junto porque a cor NÃO PODE acender fora do
   *  expediente — sem ela o cliente contaria a madrugada como espera e tudo amanheceria
   *  vermelho. `janelaDiasSemana` é bitmask: bit 0 = domingo … bit 6 = sábado. */
  janelaHoraInicio: number;
  janelaHoraFim: number;
  janelaDiasSemana: number;
  /** Feriados dos últimos 30 dias ('YYYY-MM-DD'). O navegador não tem como saber que a
   *  terça-feira foi feriado, e sem isso o desconto do tempo útil erra o dia inteiro. */
  feriadosRecentes: string[];
  /** Mensagens que entraram atrasadas nas últimas 24h, por queda do WhatsApp ou da API.
   *  `null` no caso normal. Vem no poll de 45s para o aviso aparecer sozinho — sem isso o
   *  vendedor só descobriria recarregando a página que dez conversas mudaram debaixo dele. */
  recuperacao: AvisoRecuperacao | null;
}

// ---------------------------------------------------------------- meu dia
export type TipoAcao = 'responder' | 'lembrete';

/** Uma linha do plano do dia. `aguardandoDesde` e `minutosUteis` vêm JUNTOS de propósito:
 *  o timestamp para o cliente pintar a cor (que envelhece sozinha) e os minutos ÚTEIS já
 *  descontados do que estava fora do expediente — desconto que depende dos feriados, e o
 *  navegador não os tem sem pedir. */
export interface AcaoDoDia {
  tipo: TipoAcao;
  id: number;
  contatoId: number;
  contatoNome: string;
  telefone: string;
  titulo: string;
  conversaId: number | null;
  aguardandoDesde: string | null;
  minutosUteis: number | null;
  /** Espera mais velha que a janela de feriados carregada: `minutosUteis` vem nulo de propósito,
   *  porque o número sairia sem descontar feriados antigos — maior que o real e com cara de
   *  exato. A tela mostra "mais de 30 dias". */
  esperaAcimaDaJanela: boolean;
  horaAlvo: string | null;
  dataAlvo: string | null;
  atrasado: boolean;
}

export interface MeuDia {
  acoes: AcaoDoDia[];
  respondendo: number;
  lembretes: number;
}

// ---------------------------------------------------------------- dashboard
export interface EtapaFunilDto {
  etapaId: number;
  nome: string;
  ordem: number;
  cor: string;
  contatos: number;
  valor: number;
}

// Os tipos do modo demonstração fictício (IndicadorDemo, EtapaFunilDemo, OrigemDemo,
// AtividadeDemo, TarefaDemo, PontoSerieDemo, DashboardDemo) foram REMOVIDOS junto com
// `/api/dashboard/demo`. A demonstração agora é um tenant com dados reais — ver docs/PI-4b.md.

// ---------------------------------------------------------------- série temporal (REAL)
export type AgrupamentoSerie = 'dia' | 'semana' | 'mes';

/** Um ponto da série real.
 *
 *  `tempoRespostaMinutos` é o único nullable, e é de propósito: contagem e dinheiro em período
 *  vazio valem zero (é um fato), mas MÉDIA em período vazio não vale zero — zero minuto diria
 *  "respondeu na hora" e a métrica mostraria seu melhor número no dia em que ninguém trabalhou.
 *  O período em si nunca falta; é isso que impede o gráfico de mentir sobre a tendência. */
export interface PontoSerieReal {
  data: string;
  leads: number;
  vendas: number;
  faturamento: number;
  tempoRespostaMinutos: number | null;
}

export interface SerieTemporalDto {
  de: string;
  ate: string;
  agrupamento: AgrupamentoSerie;
  pontos: PontoSerieReal[];
}

// ---------------------------------------------------------------- atividades (REAL)
export type TipoAtividadeReal = 'mensagem' | 'venda' | 'lembrete' | 'contato';

export interface Atividade {
  tipo: TipoAtividadeReal;
  /** `tipo:id` — desempate estável do cursor, porque o feed une quatro tabelas. */
  chave: string;
  quando: string;
  contatoId: number;
  contatoNome: string;
  titulo: string;
  detalhe: string | null;
  valor: number | null;
  responsavelId: number | null;
  responsavelNome: string | null;
}

export interface PaginaAtividades {
  itens: Atividade[];
  temMais: boolean;
}

/** De onde vêm os leads. SEM cor: a paleta é decisão de apresentação e mora no cliente —
 *  diferente da etapa do funil, cuja cor o dono escolhe no cadastro. */
export interface OrigemDto {
  origem: OrigemLead;
  leads: number;
}

export interface DashboardDto {
  leadsHoje: number;
  aguardandoResposta: number;
  followUpsPendentes: number;
  vendasDoMes: number;
  faturamentoDoMes: number;
  /** Fração de 0 a 1 (ganhos ÷ fechados do mês). */
  taxaConversao: number;
  funil: EtapaFunilDto[];
  origens: OrigemDto[];
}

// ---------------------------------------------------------------- lembretes
export type OrigemLembrete = 'automatico' | 'manual';
export type StatusLembrete = 'pendente' | 'concluido' | 'cancelado';

export interface LembreteDto {
  id: number;
  contatoId: number;
  contatoNome: string;
  conversaId: number | null;
  origem: OrigemLembrete;
  status: StatusLembrete;
  dataAlvo: string;
  horaAlvo: string | null;
  titulo: string;
  observacao: string | null;
  enviaMensagem: boolean;
  responsavelId: number | null;
  responsavelNome: string | null;
  concluidoEm: string | null;
}

// ---------------------------------------------------------------- onboarding
/** Um passo dos primeiros passos. `concluido` é DERIVADO do estado real a cada leitura, nunca
 *  lido de uma flag — empresa cujo WhatsApp caiu volta a ver o passo 1 aceso. */
export interface PassoOnboarding {
  chave: 'conexao' | 'equipe' | 'primeira_mensagem';
  titulo: string;
  descricao: string;
  concluido: boolean;
  /** O dono pulou. Só o passo da equipe aceita. */
  dispensado: boolean;
  /** Para onde o passo leva. NULL no passo 3 — ele é espera, não ação. */
  rota: string | null;
  rotuloAcao: string | null;
}

export interface Onboarding {
  passos: PassoOnboarding[];
  concluidos: number;
  total: number;
  completo: boolean;
  dispensado: boolean;
  /** Falta passo E o dono não fechou o painel. */
  mostrar: boolean;
  /** Métrica INTERNA. A tela não exibe e nada promete prazo de implantação. */
  minutosAteAPrimeiraMensagem: number | null;
}

// ---------------------------------------------------------------- configuração
export interface FusoDisponivel {
  id: string;
  rotulo: string;
  offsetAtual: string;
}

export interface ConfiguracaoEmpresa {
  nome: string;
  documento: string | null;
  fusoHorario: string;
  /** Sigla da UF. Só serve para semear os feriados estaduais; nula = só nacionais. */
  uf: string | null;
  janelaHoraInicio: number;
  janelaHoraFim: number;
  /** Bitmask: bit 0 = domingo … bit 6 = sábado. 126 = seg a sáb. */
  janelaDiasSemana: number;
  /** Minutos ÚTEIS. Zero DESLIGA a faixa — é comportamento legítimo. */
  semaforoAmareloMinutos: number;
  semaforoVermelhoMinutos: number;
  /** Dias de conversa parada até o follow-up. Mínimo 1. */
  diasSemRespostaFollowUp: number;
  /** Dias até a venda ser concluída sozinha (NEG-2). ZERO = concluir na hora, e é valor
   *  legítimo: padaria, salão, balcão — a venda nasce e termina no mesmo atendimento. */
  diasParaConcluirVenda: number;
}

export interface FeriadoDto {
  id: number;
  data: string;
  nome: string;
  abrangencia: 'nacional' | 'estadual' | 'manual';
  ehManual: boolean;
  /** Só para feriado nacional: a empresa marcou que trabalha nesse dia. */
  ignorado: boolean;
}

export interface MinhaConta {
  id: number;
  nome: string;
  email: string;
  papel: PapelUsuario;
  empresaNome: string;
}

// ---------------------------------------------------------------- caixa
export interface ConversaResumo {
  id: number;
  contatoId: number;
  contatoNome: string;
  telefone: string;
  ultimaMensagemPrevia: string | null;
  ultimaMensagemDirecao: DirecaoMensagem | null;
  ultimaMensagemEm: string;
  /** TIMESTAMP, não cor. Ver nucleo/semaforo.ts. */
  aguardandoDesde: string | null;
  naoLidas: number;
  status: StatusConversa;
  responsavelId: number | null;
  responsavelNome: string | null;
  etapaId: number;
  etapaNome: string;
}

export interface MensagemDto {
  id: number;
  direcao: DirecaoMensagem;
  texto: string | null;
  /** 0=erro, 1=enviado, 2=servidor, 3=entregue, 4=lido. */
  ack: number | null;
  enviadaEm: string | null;
  recebidaEm: string | null;
  expiradaEm: string | null;
  erro: string | null;
  tipoMidia: TipoMidia;
  midiaNome: string | null;
  midiaMime: string | null;
  /** Tamanho do anexo, para a tela mostrar "proposta.pdf · 340 KB". */
  midiaBytes: number | null;
  /** Duração do áudio em segundos; `null` nas outras mídias. Aparece no player ANTES de tocar:
   *  ouvir 90 segundos é uma decisão, e o vendedor precisa tomá-la olhando. */
  midiaDuracaoSegundos: number | null;
  enviadoPor: number | null;
  enviadoPorNome: string | null;
  deLembrete: boolean;
  /** Instante em que esta mensagem ATRASADA foi gravada; null no caso normal (REC-1).
   *  Ela aparece na posição cronológica dela — o carimbo só explica por que surgiu agora
   *  num ponto da thread que já tinha passado. */
  recuperadaEm: string | null;
}

/** Uma venda do histórico (NEG-1). `canceladaEm` vem preenchido em vez de a linha sumir: a
 *  lista a mostra riscada, porque "o valor mudou de 5.000 para 3.000" é informação, e uma linha
 *  que desaparece não explica nada a quem confere o mês depois. */
export interface VendaDto {
  id: number;
  valor: number;
  fechadaEm: string;
  responsavelId: number | null;
  responsavelNome: string | null;
  observacao: string | null;
  canceladaEm: string | null;
  /** `fechada`, `concluida` ou `cancelada` (NEG-2). */
  status: 'fechada' | 'concluida' | 'cancelada';
  concluidaEm: string | null;
}

/** Um evento da trilha de auditoria (AUD-1).
 *
 *  `alteracoes` chega como JSON CRU, de propósito: a tradução para português é texto de
 *  interface, muda com a redação do produto, e fazê-la no servidor obrigaria a um deploy de
 *  backend para corrigir uma frase. */
export interface EventoTrilha {
  id: number;
  entidade: string;
  entidadeId: number;
  acao: string;
  alteracoes: string;
  usuarioId: number | null;
  usuarioNome: string | null;
  ator: string;
  quando: string;
}

/** O aviso de mensagens recuperadas. `null` quando não houve queda nas últimas 24h. */
export interface AvisoRecuperacao {
  mensagens: number;
  conversas: number;
  /** O intervalo em que o CLIENTE escreveu — não o instante em que gravamos. */
  de: string;
  ate: string;
}

export interface RespostaEnviada {
  mensagemId: number;
  /** false = registrada mas não chegou (WhatsApp fora). A mensagem aparece na thread
   *  marcada como "não chegou" — não é erro de requisição. */
  enviada: boolean;
  erro: string | null;
}

// ---------------------------------------------------------------- conexão
export interface Conexao {
  id: number;
  nome: string;
  instanceName: string;
  numero: string | null;
  numeroAnterior: string | null;
  perfilNome: string | null;
  perfilFotoUrl: string | null;
  status: StatusConexao;
  conectadoEm: string | null;
  desconectadoEm: string | null;
  /** Quantas conversas apontam para este número. É a contagem CRUA, a mesma que a FK enxerga. */
  conversas: number;
  /** Vêm do SERVIDOR, não são deduzidos aqui: só o banco sabe se há conversa apontando para a
   *  conexão. Sem isso a tela ofereceria um botão que às vezes devolve erro — a pior forma de
   *  dizer "não pode". */
  podeRemover: boolean;
  motivoNaoRemove: string | null;
}

/** A lista + o que o PLANO permite. O limite vem junto porque a tela precisa dele para decidir
 *  se mostra "adicionar" — e um limite que a tela adivinha diverge do que o servidor aplica no
 *  dia em que o contrato muda. */
export interface Conexoes {
  itens: Conexao[];
  limite: number;
  podeAdicionar: boolean;
}

export interface StatusConexaoDto {
  instanceName: string;
  /** Estado cru da Evolution: open | connecting | close | nao_criada | offline. */
  estado: string;
  conectado: boolean;
}

export interface QrCode {
  base64: string | null;
  codigo: string | null;
  pairingCode: string | null;
  estado: string;
  conectado: boolean;
}

export interface SaudeConexao {
  enviadasHoje: number;
  pendentes: number;
  expiradas: number;
  falhasHoje: number;
}

// ---------------------------------------------------------------- canais (QR / link)

/** Um canal de captação por QR Code ou link rastreável.
 *
 *  `link`, `texto`, `nomeArquivo` e `podeRemover` vêm do SERVIDOR e não são montados aqui: o link
 *  depende do número da conexão, o texto é a mesma string que o webhook vai procurar, e só o
 *  banco sabe se já chegou lead por este canal. */
export interface CanalDto {
  id: number;
  nome: string;
  codigo: string;
  conexaoId: number;
  conexaoNome: string;
  /** Nulo quando a conexão perdeu o pareamento — e aí `link` também é nulo e o QR não sai. */
  numero: string | null;
  origem: OrigemLead;
  ativo: boolean;
  leadsRecebidos: number;
  link: string | null;
  texto: string;
  /** Sem extensão. O download é por blob, e blob não carrega `Content-Disposition`. */
  nomeArquivo: string;
  podeRemover: boolean;
  motivoNaoRemove: string | null;
  criadoEm: string;
}

export interface ConexaoParaCanal {
  id: number;
  nome: string;
  numero: string;
}

export interface Canais {
  itens: CanalDto[];
  /** Só as conexões com número pareado: sem número, o link sai quebrado. */
  conexoes: ConexaoParaCanal[];
  podeCriar: boolean;
  /** Soma dos leads atribuídos. É PISO, não total — quem apagou o código antes de enviar
   *  entrou como `whatsapp` e não aparece aqui. */
  leadsAtribuidos: number;
}

// ---------------------------------------------------------------- webhook de saída

export type EventoWebhook =
  | 'lead.criado' | 'lead.movido' | 'venda.fechada' | 'venda.perdida'
  | 'mensagem.recebida' | 'webhook.teste';

export type StatusEntrega = 'pendente' | 'entregue' | 'falhou';

/** A configuração do webhook. **Nunca traz o segredo** — ele sai uma vez, na criação e ao ser
 *  regerado. Um segredo que a tela busca a cada carregamento vive no histórico do navegador e no
 *  cache do proxy. */
export interface WebhookDto {
  id: number;
  url: string;
  ativo: boolean;
  somenteIds: boolean;
  emLeadCriado: boolean;
  emLeadMovido: boolean;
  emVendaFechada: boolean;
  emVendaPerdida: boolean;
  emMensagemRecebida: boolean;
  criadoEm: string;
}

export interface EntregaWebhookDto {
  id: number;
  evento: EventoWebhook;
  status: StatusEntrega;
  tentativas: number;
  codigoResposta: number | null;
  erro: string | null;
  proximaTentativaEm: string | null;
  entregueEm: string | null;
  criadoEm: string;
  /** O corpo exato que foi assinado. Vai junto porque "o cliente diz que não recebeu" costuma
   *  terminar em "o que exatamente vocês mandaram?". */
  payload: string;
  podeReenviar: boolean;
}

export interface PainelWebhook {
  webhook: WebhookDto | null;
  entregas: EntregaWebhookDto[];
}

export interface SalvarWebhook {
  url: string;
  ativo: boolean;
  somenteIds: boolean;
  emLeadCriado: boolean;
  emLeadMovido: boolean;
  emVendaFechada: boolean;
  emVendaPerdida: boolean;
  emMensagemRecebida: boolean;
}

/** O segredo, entregue uma vez. `novo` distingue "acabei de criar" de "regerei". */
export interface SegredoRevelado {
  id: number;
  segredo: string;
  novo: boolean;
}

export interface ResultadoTeste {
  ok: boolean;
  codigo: number | null;
  erro: string | null;
}

// ---------------------------------------------------------------- equipe
export interface UsuarioEquipe {
  id: number;
  nome: string;
  email: string;
  papel: PapelUsuario;
  status: StatusUsuario;
  ultimoAcessoEm: string | null;
}

export interface TokenGerado {
  usuarioId: number;
  token: string;
}

export interface ConviteInfo {
  nome: string;
  email: string;
  empresaNome: string;
}

// ---------------------------------------------------------------- realtime
export interface MensagemPainel {
  id: number;
  conversaId: number;
  contatoId: number;
  contatoNome: string;
  previa: string | null;
  direcao: DirecaoMensagem;
  em: string;
}

export interface ConversaPainel {
  id: number;
  contatoId: number;
  contatoNome: string;
  telefone: string;
}

export interface ContatoPainel {
  id: number;
  nome: string;
  telefone: string;
  etapaId: number;
}

export interface ConexaoPainel {
  id: number;
  status: StatusConexao;
  numero: string | null;
  numeroAnterior: string | null;
}

export interface StatusMensagemPainel {
  mensagemId: number;
  ack: number;
}

// ---------------------------------------------------------------- captação por formulário

/** Um formulário de captação publicado no site do cliente.
 *
 *  A `chave` é o que abre um endpoint de ESCRITA na internet — ela aparece na tela para ser
 *  copiada para o HTML, e some do ar assim que é regerada. */
export interface FormularioDto {
  id: number;
  nome: string;
  chave: string;
  dominioPermitido: string | null;
  ativo: boolean;
  leadsRecebidos: number;
  criadoEm: string;
}

// ---------------------------------------------------------------- configuração do funil

/** Uma etapa na tela de CONFIGURAÇÃO do funil (a do quadro é `EtapaFunil`).
 *
 *  `contatos` é a contagem CRUA — inclui perdido e anonimizado, que continuam apontando para a
 *  etapa e travando a remoção pela FK. É diferente do número que o kanban mostra, e é de
 *  propósito: aqui o número responde "o que impede de apagar". */
export interface EtapaConfigDto {
  id: number;
  nome: string;
  ordem: number;
  cor: string;
  eGanho: boolean;
  contatos: number;
}
