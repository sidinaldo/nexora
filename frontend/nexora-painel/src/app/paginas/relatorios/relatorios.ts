import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { AuthServico } from '../../nucleo/servicos/auth.servico';
import { ToastServico } from '../../nucleo/toast/toast.servico';
import { baixarBlob } from '../../nucleo/download';
import { GraficoBarras, BarraGrafico } from '../../nucleo/graficos/grafico-barras';
import { GraficoLinha, PontoSerie } from '../../nucleo/graficos/grafico-linha';
import { Ajuda } from '../../nucleo/ajuda/ajuda';
import {
  ComparativoVendas, FiltroRelatorio, IndicadorComparativo,
  LinhaCanalVenda, LinhaClienteRecorrente, LinhaMotivoPerda, LinhaOrigem,
  LinhaTempoResposta,
  LinhaVendedor, EtapaDoFunil, OpcaoEtapa, OpcoesRelatorio, RelatoriosServico, RelatorioFunil,
  ComparativoNps, RelatorioNps, RelatorioVendas,
  AtalhoRespostas, FiltroRespostas, LinhaRespostaNps
} from '../../nucleo/servicos/relatorios.servico';

// ⚠️ `OpcoesRelatorio` E `OpcaoFiltro` ERAM REDECLARADOS AQUI, cópia idêntica da do serviço —
//    até deixarem de ser idênticos. O serviço passou a devolver o funil de cada etapa (FUN-1) e
//    esta cópia continuou com a forma antiga: o `GET` trazia o campo, o TypeScript afirmava que
//    ele não existia, e a tela não tinha como agrupar. Vale para a interface inteira, não só para
//    o campo novo — duas declarações da mesma coisa divergem, é só questão de quando.

type Atalho = 'hoje' | '7' | '30' | 'mes' | 'mes-anterior' | 'livre';

/** ===================== RELATÓRIOS (BLOCO 14) =====================
 *
 *  O dashboard responde "como está agora". Esta tela responde "o que aconteceu no período" — e é
 *  o que o dono abre uma vez por semana.
 *
 *  UMA BARRA DE FILTROS para os sete relatórios, e uma carga só ao aplicar. Sete telas separadas
 *  fariam o dono reconfigurar o período sete vezes para responder uma pergunta que é uma só.
 *
 *  ⚠️ O RECORTE POR PAPEL NÃO ESTÁ AQUI. O seletor de responsável vem travado para o vendedor
 *  porque a API devolve só ele em `/opcoes` — não porque a tela decide. Quem protege é o
 *  servidor; se fosse a tela, bastaria trocar o parâmetro na requisição.
 *  ============================================================== */
@Component({
  selector: 'app-relatorios',
  imports: [FormsModule, RouterLink, DatePipe, GraficoBarras, GraficoLinha, Ajuda],
  templateUrl: './relatorios.html',
  styleUrl: './relatorios.css'
})
export class Relatorios implements OnInit {
  private api = inject(RelatoriosServico);
  private toast = inject(ToastServico);
  auth = inject(AuthServico);

  readonly porPagina = 20;

  // ---------------------------------------------------------------- filtros
  atalho = signal<Atalho>('30');
  de = signal('');
  ate = signal('');
  agrupamento = signal<'dia' | 'semana' | 'mes'>('dia');
  responsavelId = signal<number | null>(null);
  origem = signal<string | null>(null);
  etapaId = signal<number | null>(null);
  status = signal<'ganha' | 'concluida' | 'cancelada' | null>(null);
  motivoPerda = signal<string | null>(null);
  valorMin = signal<number | null>(null);
  valorMax = signal<number | null>(null);

  opcoes = signal<OpcoesRelatorio>({ responsaveis: [], etapas: [], motivosPerda: [] });

  /** As etapas agrupadas por funil, NA ORDEM EM QUE A API MANDOU.
   *
   *  ⚠️ AGRUPA PERCORRENDO EM SEQUÊNCIA, quebrando quando o funil muda — não por `Map` nem por
   *  nome. A API já devolve ordenado por funil e depois por etapa; reagrupar por chave jogaria
   *  essa ordem fora e as etapas sairiam embaralhadas dentro do próprio grupo, que é metade do
   *  defeito que isto conserta. */
  etapasPorFunil = computed(() => {
    const grupos: { funil: string; etapas: OpcaoEtapa[] }[] = [];

    for (const e of this.opcoes().etapas) {
      const ultimo = grupos[grupos.length - 1];
      if (ultimo && ultimo.etapas[0].pipelineId === e.pipelineId) ultimo.etapas.push(e);
      else grupos.push({ funil: e.pipelineNome, etapas: [e] });
    }

    return grupos;
  });

  /** As origens são enum fechado no servidor; a lista pode viver aqui sem risco de divergir —
   *  um valor inventado é recusado com 400, não ignorado em silêncio. */
  readonly origens = [
    'instagram', 'facebook', 'whatsapp', 'google', 'site', 'qrcode', 'indicacao', 'meta_ads',
    'manual', 'outro'
  ];

  // ---------------------------------------------------------------- dados
  carregando = signal(false);
  erro = signal('');

  vendas = signal<RelatorioVendas | null>(null);
  vendedores = signal<LinhaVendedor[]>([]);
  origensLinhas = signal<LinhaOrigem[]>([]);
  canaisLinhas = signal<LinhaCanalVenda[]>([]);
  funil = signal<RelatorioFunil | null>(null);
  tempos = signal<LinhaTempoResposta[]>([]);
  perdas = signal<LinhaMotivoPerda[]>([]);
  pesquisa = signal<RelatorioNps | null>(null);

  // ---------------------------------------------------------------- respostas (NPS-1 3.3 / 3.4)
  atalhoRespostas = signal<AtalhoRespostas>('Nenhum');
  faixaRespostas = signal<FiltroRespostas['faixa']>(null);
  comprouDeNovo = signal<boolean | null>(null);
  diasSemCompra = signal(60);

  respostas = signal<LinhaRespostaNps[]>([]);
  respostasTotal = signal(0);
  respostasPagina = signal(1);

  recorrentes = signal<LinhaClienteRecorrente[]>([]);
  recorrentesTotal = signal(0);
  recorrentesPagina = signal(1);

  baixando = signal<string | null>(null);

  ngOnInit() {
    this.aplicarAtalho('30');
    this.api.opcoes().subscribe({
      next: o => this.opcoes.set(o),
      // Um seletor vazio é menos ruim que uma tela que não abre: os relatórios não dependem das
      // listas para funcionar, só o filtro fica sem sugestão.
      error: () => { }
    });
    this.carregar();
  }

  // ---------------------------------------------------------------- período
  /** Os atalhos escrevem nos campos de data em vez de guardar um modo à parte: o dono vê QUAL
   *  intervalo foi escolhido, e pode ajustar uma ponta sem perder a outra. */
  aplicarAtalho(a: Atalho) {
    this.atalho.set(a);
    if (a === 'livre') return;

    const hoje = new Date();
    const iso = (d: Date) => d.toISOString().slice(0, 10);

    if (a === 'hoje') {
      this.de.set(iso(hoje));
      this.ate.set(iso(hoje));
    } else if (a === '7' || a === '30') {
      const dias = Number(a);
      const inicio = new Date(hoje);
      inicio.setDate(inicio.getDate() - (dias - 1));
      this.de.set(iso(inicio));
      this.ate.set(iso(hoje));
    } else if (a === 'mes') {
      this.de.set(iso(new Date(hoje.getFullYear(), hoje.getMonth(), 1)));
      this.ate.set(iso(hoje));
    } else {
      // Mês anterior INTEIRO: dia 0 do mês corrente é o último dia do anterior.
      const inicio = new Date(hoje.getFullYear(), hoje.getMonth() - 1, 1);
      const fim = new Date(hoje.getFullYear(), hoje.getMonth(), 0);
      this.de.set(iso(inicio));
      this.ate.set(iso(fim));
    }

    // Período longo em dias vira ilegível e estoura o teto de pontos do servidor.
    if (a === 'mes-anterior' || a === 'mes') this.agrupamento.set('dia');
  }

  editouData() { this.atalho.set('livre'); }

  filtro(): FiltroRelatorio {
    return {
      de: this.de(),
      ate: this.ate(),
      agrupamento: this.agrupamento(),
      responsavelId: this.responsavelId(),
      origem: this.origem(),
      etapaId: this.etapaId(),
      status: this.status(),
      motivoPerda: this.motivoPerda(),
      valorMin: this.valorMin(),
      valorMax: this.valorMax()
    };
  }

  /** ===================== OS SETE SECUNDÁRIOS, RECOLHIDOS =====================
   *  Eram 11 campos numa grade só: a 1440px dá cinco colunas, logo TRÊS faixas verticais — mais os
   *  atalhos em cima e as ações embaixo, cinco faixas antes de o primeiro número aparecer.
   *
   *  ⚠️ É BLOCO EXPANSÍVEL, NÃO POPOVER. `paginas.celular.spec.ts` varre toda tela e reprova
   *  `position: fixed`/`sticky` fora de `.overlay` e `.pilha` — e há decisão registrada no mesmo
   *  sentido em `seletor-etiquetas.ts` ("MODAL, e não popover"). O padrão da casa para "abrir mais"
   *  é o bloco no fluxo, que Formulários, Anúncios e Webhook já usam. */
  maisFiltros = signal(false);

  /** ===================== UMA EXPORTAÇÃO, NÃO SETE (REL-1) =====================
   *  Eram sete botões idênticos, um por cartão, todos chamando a mesma rota com um nome diferente.
   *
   *  ⚠️ E O ESTADO JÁ ERA DE UM SÓ. `baixando` sempre foi global, e `exportar()` começa com
   *  `if (this.baixando()) return` — então um download em curso recusava os outros seis EM
   *  SILÊNCIO, enquanto o `[disabled]` só acendia no botão clicado. Com um controle, o que a tela
   *  mostra passa a ser o que o código faz.
   *  =========================================================================== */
  readonly relatoriosExportaveis = [
    { id: 'vendas', nome: 'Vendas no período' },
    { id: 'vendedores', nome: 'Desempenho por vendedor' },
    { id: 'origens', nome: 'Origem dos leads' },
    { id: 'funil', nome: 'Funil' },
    { id: 'tempo-resposta', nome: 'Tempo de resposta' },
    { id: 'perdas', nome: 'Motivos de perda' },
    { id: 'recorrentes', nome: 'Clientes recorrentes' },
    { id: 'nps', nome: 'Pesquisa pós-venda (NPS)' }
  ];

  exportarQual = signal('vendas');

  /** Quantos dos sete estão RECORTANDO de verdade — não quantos estão escondidos.
   *
   *  ⚠️ É o que faz o botão avisar, fechado, que há recorte em vigor. Um contador de campos
   *  ocultos diria "7" para sempre e não informaria nada.
   *
   *  A lista é a MESMA de `limpar()`, logo abaixo, e de propósito: são as duas metades da mesma
   *  definição de "filtro secundário", e se divergirem o dono limpa um filtro que o contador ainda
   *  conta — ou pior, o contrário. */
  filtrosAtivos = computed(() =>
    [this.responsavelId(), this.origem(), this.etapaId(), this.status(),
     this.motivoPerda(), this.valorMin(), this.valorMax()]
      .filter(v => v !== null).length);

  limpar() {
    this.responsavelId.set(null);
    this.origem.set(null);
    this.etapaId.set(null);
    this.status.set(null);
    this.motivoPerda.set(null);
    this.valorMin.set(null);
    this.valorMax.set(null);
    this.aplicarAtalho('30');
    this.carregar();
  }

  // ---------------------------------------------------------------- carga
  carregar() {
    if (this.de() > this.ate()) {
      this.erro.set('A data inicial não pode ser depois da final.');
      return;
    }

    this.carregando.set(true);
    this.erro.set('');
    this.recorrentesPagina.set(1);

    const f = this.filtro();

    // Sete chamadas em paralelo, e não um endpoint que devolve tudo: cada relatório tem custo
    // próprio, e o mais caro (tempo de resposta, que varre mensagens) não pode segurar os outros
    // seis na tela. Cada seção aparece quando fica pronta.
    const pedidos: [string, () => void][] = [
      ['vendas', () => this.api.vendas(f).subscribe({ next: r => this.vendas.set(r), error: e => this.falhou(e) })],
      ['vendedores', () => this.api.vendedores(f).subscribe({ next: r => this.vendedores.set(r), error: e => this.falhou(e) })],
      ['origens', () => {
        this.api.origens(f).subscribe({ next: r => this.origensLinhas.set(r), error: e => this.falhou(e) });
        // A segunda leitura do MESMO cartão, em requisição própria. Um `forkJoin` faria a tabela
        // de cima esperar a de baixo — e ela é a que o dono olha primeiro.
        this.api.canais(f).subscribe({ next: r => this.canaisLinhas.set(r), error: () => this.canaisLinhas.set([]) });
      }],
      ['funil', () => this.api.funil(f).subscribe({ next: r => this.funil.set(r), error: e => this.falhou(e) })],
      ['tempo', () => this.api.tempoResposta(f).subscribe({ next: r => this.tempos.set(r), error: e => this.falhou(e) })],
      ['perdas', () => this.api.perdas(f).subscribe({ next: r => this.perdas.set(r), error: e => this.falhou(e) })],
      ['nps', () => this.api.nps(f).subscribe({ next: r => this.pesquisa.set(r), error: e => this.falhou(e) })],
      ['respostas', () => this.paginaRespostas(1)],
      ['recorrentes', () => this.paginaRecorrentes(1)]
    ];

    for (const [, executar] of pedidos) executar();
    this.carregando.set(false);
  }

  private falhou(e: { error?: { erro?: string } }) {
    this.erro.set(e.error?.erro ?? 'Não foi possível carregar o relatório.');
  }

  paginaRecorrentes(pagina: number) {
    this.api.recorrentes(this.filtro(), pagina, this.porPagina).subscribe({
      next: p => {
        this.recorrentes.set(p.itens);
        this.recorrentesTotal.set(p.totalCount);
        this.recorrentesPagina.set(p.pagina);
        this.totalPaginasRecorrentes.set(p.totalPaginas);
      },
      error: e => this.falhou(e)
    });
  }

  /** Quantas páginas há, do servidor (AUD-XX, #21). A tela dividia o total pelo tamanho. */
  totalPaginasRecorrentes = signal(1);

  /** A lista de respostas. Os filtros dela moram aqui, e não na barra: valem só para esta tabela,
   *  e pôr "faixa" na barra faria o dono achar que ela recorta o NPS também. */
  paginaRespostas(pagina: number) {
    const filtro: FiltroRespostas = {
      faixa: this.faixaRespostas(),
      comprouDeNovo: this.comprouDeNovo(),
      atalho: this.atalhoRespostas(),
      diasSemCompra: this.diasSemCompra()
    };

    this.api.respostasNps(this.filtro(), filtro, pagina, this.porPagina).subscribe({
      next: p => {
        this.respostas.set(p.itens);
        this.respostasTotal.set(p.totalCount);
        this.respostasPagina.set(p.pagina);
        this.totalPaginasRespostas.set(p.totalPaginas);
      },
      error: e => this.falhou(e)
    });
  }

  escolherAtalho(a: AtalhoRespostas) {
    this.atalhoRespostas.set(a);
    this.paginaRespostas(1);
  }

  /** Quantas páginas há, do servidor (AUD-XX, #21). */
  totalPaginasRespostas = signal(1);

  /** A frase do vazio diz O QUE estava vazio. "Nenhuma resposta" num atalho faria o dono achar
   *  que a pesquisa não funciona, quando a notícia é boa: ninguém ficou sem retorno. */
  textoSemRespostas = computed(() => {
    const a = this.atalhoRespostas();
    if (a === 'PromotoresQueNaoVoltaram') return 'Nenhum promotor sem compra nesse prazo.';
    if (a === 'DetratoresSemRetorno') return 'Todo detrator já recebeu uma mensagem da equipe depois da nota.';
    return 'Nenhuma resposta no período com esses filtros.';
  });

  // ---------------------------------------------------------------- gráficos
  /** A barra clara é o faturamento; a escura, a parte já concluída. Duas barras lado a lado
   *  pediriam ao leitor que somasse mentalmente para saber o total do dia. */
  /** ===================== A VENDA NO PERÍODO É UMA CURVA, NÃO UMA FILEIRA =====================
   *  Eram barras verticais. Num período de 30 dias isso dá 30 colunas finas com picos isolados, e a
   *  continuidade — que é a pergunta ("está subindo?") — some entre elas.
   *
   *  ⚠️ O COMPONENTE JÁ EXISTIA e já faz tudo: área preenchida, linha, média móvel tracejada e
   *  etiqueta em reais ao passar o mouse E ao toque. É o mesmo que o dashboard desenha. Nenhuma
   *  biblioteca de gráfico entrou — o projeto desenha SVG à mão, e isso não muda por um cartão.
   *
   *  ⚠️ O QUE SE PERDEU, DITO POR EXTENSO: a barra pintava a PARTE JÁ CONCLUÍDA em tom escuro
   *  dentro dela mesma, e `grafico-linha` não tem segunda série. O número não sumiu do produto — o
   *  KPI "Já concluído" ali em cima é exatamente ele —, mas saiu do desenho. Dar segunda série ao
   *  componente é trabalho maior que esta fase inteira, e está anotado.
   *  ========================================================================================= */
  /** A média móvel vem pronta do servidor, só no agrupamento por dia (AUD-XX). */
  serieVendas = computed<PontoSerie[]>(() =>
    (this.vendas()?.pontos ?? []).map(p => ({ data: p.periodo, valor: p.faturamento, media: p.mediaFaturamento })));


  barrasOrigem = computed<BarraGrafico[]>(() =>
    this.origensLinhas().map(o => ({ rotulo: o.origem, valor: o.valor })));

  /** ⚠️ ENTRADAS, não a foto. As duas séries vivem lado a lado no template, cada uma com o
   *  rótulo dela — misturá-las é exatamente o que produz o "no período" mentiroso. */
  /** As entradas agrupadas por funil, NA ORDEM EM QUE A API MANDOU — um gráfico por grupo.
   *
   *  ⚠️ AGRUPA EM SEQUÊNCIA, quebrando quando o funil muda; não por `Map` nem por nome. A API já
   *  devolve ordenado por funil e depois por etapa, e reagrupar por chave jogaria essa ordem fora:
   *  as barras sairiam embaralhadas dentro do próprio grupo, que é metade do defeito que isto
   *  conserta. É a mesma rotina do filtro de etapa, logo acima. */
  gruposFunilEntradas = computed<{ funil: string; barras: BarraGrafico[] }[]>(() => {
    const grupos: { funil: string; pipelineId: number; barras: BarraGrafico[] }[] = [];

    for (const e of this.funil()?.etapas ?? []) {
      const ultimo = grupos[grupos.length - 1];
      const barra = { rotulo: e.nome, valor: e.entradas };

      if (ultimo && ultimo.pipelineId === e.pipelineId) ultimo.barras.push(barra);
      else grupos.push({ funil: e.pipelineNome, pipelineId: e.pipelineId, barras: [barra] });
    }

    return grupos;
  });

  /** ===================== UMA ESCALA SÓ PARA TODOS OS GRÁFICOS =====================
   *  Aqui se lê VOLUME ("quantos entraram"), não forma. Deixando cada gráfico se normalizar pelo
   *  próprio maior, 16 entradas do Atacado desenhariam a mesma barra que 42 de Vendas — e o leitor
   *  concluiria que os dois trazem o mesmo movimento.
   *
   *  ⚠️ É O OPOSTO DA DECISÃO DO PAINEL, e de propósito: lá cada funil tem escala própria porque o
   *  que se lê é a FORMA, e um funil pequeno ao lado de um grande vira um fio de barras ilegível.
   *  Mesma tela, perguntas diferentes.
   *  ============================================================================= */
  maximoFunilEntradas = computed(() =>
    Math.max(1, ...(this.funil()?.etapas ?? []).map(e => e.entradas)));

  /** As onze barras da pesquisa, na ordem do servidor (0 a 10). O rótulo é a nota; o servidor já
   *  garante as onze, então a posição de cada barra não muda entre dois períodos. */
  barrasNps = computed<BarraGrafico[]>(() =>
    (this.pesquisa()?.distribuicao ?? []).map(f => ({ rotulo: String(f.nota), valor: f.quantas })));

  /** O NPS com sinal: "+25", "−10", "0". Sem o "+", um NPS positivo pareceria uma contagem.
   *  ⚠️ NULO VIRA FRASE, não zero: zero é um NPS real. */
  textoNps(v: number | null): string {
    if (v === null) return 'sem respostas';
    const n = v.toLocaleString('pt-BR', { maximumFractionDigits: 1 });
    return v > 0 ? `+${n}` : v < 0 ? `−${n.replace('-', '')}` : n;
  }

  /** O mesmo rótulo de `recorte()`, para o comparativo da pesquisa. */
  recorteNps(c: ComparativoNps): string {
    return `${Relatorios.dia(c.de)}–${Relatorios.dia(c.ate)} vs ` +
           `${Relatorios.dia(c.nps.anteriorDe)}–${Relatorios.dia(c.nps.anteriorAte)}`;
  }

  textoTaxa(v: number | null): string {
    return v === null ? '—' : `${v.toLocaleString('pt-BR', { maximumFractionDigits: 1 })}%`;
  }

  /** As etapas de AGORA agrupadas, para a tabela. Mesma rotina das entradas. */
  gruposFunilAgora = computed<{ funil: string; etapas: EtapaDoFunil[] }[]>(() => {
    const grupos: { funil: string; pipelineId: number; etapas: EtapaDoFunil[] }[] = [];

    for (const e of this.funil()?.etapas ?? []) {
      const ultimo = grupos[grupos.length - 1];
      if (ultimo && ultimo.pipelineId === e.pipelineId) ultimo.etapas.push(e);
      else grupos.push({ funil: e.pipelineNome, pipelineId: e.pipelineId, etapas: [e] });
    }

    return grupos;
  });

  // ---------------------------------------------------------------- exportação
  exportar(nome: string) {
    if (this.baixando()) return;
    this.baixando.set(nome);

    this.api.csv(nome, this.filtro()).subscribe({
      next: blob => {
        this.baixando.set(null);
        baixarBlob(`${nome}-${this.de()}-a-${this.ate()}.csv`, blob);
      },
      error: () => {
        this.baixando.set(null);
        this.toast.erro('Não foi possível gerar o arquivo.');
      }
    });
  }

  // ---------------------------------------------------------------- comparação (CMP-1)
  /** Os meses por extenso curto. Lista fixa em vez de `DatePipe` com `MMM`: ela não depende do
   *  locale estar registrado, e o rótulo tem três letras garantidas — "set" e não "set." nem
   *  "Sep", que é o que sai quando o locale não entrou no bundle. */
  private static readonly Meses = [
    'jan', 'fev', 'mar', 'abr', 'mai', 'jun', 'jul', 'ago', 'set', 'out', 'nov', 'dez'
  ];

  private static mes(iso: string): string {
    // `iso` é `yyyy-MM-dd` (o `DateOnly` do servidor). Partir a string evita o fuso do navegador
    // mover o dia — `new Date('2026-08-31')` em UTC-3 vira 30/08.
    return Relatorios.Meses[Number(iso.slice(5, 7)) - 1] ?? '?';
  }

  private static dia(iso: string): string {
    return `${iso.slice(8, 10)}/${Relatorios.mes(iso)}`;
  }

  /** ===================== A LINHA DE COMPARAÇÃO =====================
   *  `↓ 25% · R$ 24.000 em ago`, e não "contra R$ 24.000 em agosto · −25%".
   *
   *  ⚠️ A ORDEM É PELA LARGURA. O KPI tem ~210px numa fileira de cinco (`.kpis` é
   *  `auto-fit minmax(140px, 1fr)`): a variação vem primeiro porque é o que a pessoa procura, e o
   *  texto longo não cabe. Medido no preview antes de escrever.
   *
   *  ⚠️ `semAnterior` É POR INDICADOR, e é o que impede a tela de dizer a coisa errada: anterior
   *  zero em "cancelado" não é "novo", é "sem cancelamento".
   *  ============================================================== */
  comparar(i: IndicadorComparativo, semAnterior: string, formato: 'moeda' | 'conta' | 'decimal'): string {
    let antes: string;
    if (formato === 'moeda') antes = this.moeda(i.anterior);
    else if (formato === 'decimal') antes = i.anterior.toLocaleString('pt-BR', { maximumFractionDigits: 1 });
    else antes = String(i.anterior);

    // ⚠️ SÓ DIZ O MÊS QUANDO O PERÍODO ANTERIOR CABE NUM MÊS. Com os atalhos de 7 e 30 dias a
    // janela atravessa a virada — 08/ago a 05/set —, e nomear só o mês do FIM culpa setembro por
    // uma janela que começou em agosto. Foi o que apareceu na tela: "sem venda em set" para um
    // período que era quase todo de agosto.
    const mesmoMes = i.anteriorDe.slice(0, 7) === i.anteriorAte.slice(0, 7);
    const ondeCurto = mesmoMes ? ` em ${Relatorios.mes(i.anteriorAte)}` : '';
    const ondeLongo = mesmoMes ? ` em ${Relatorios.mes(i.anteriorAte)}` : ' no período anterior';

    if (i.variacaoPercentual === null) return `${semAnterior}${ondeLongo}`;
    if (i.tendencia === 'estavel') return `igual a ${antes}${ondeLongo}`;

    const seta = i.tendencia === 'subiu' ? '↑' : '↓';
    const pct = Math.abs(i.variacaoPercentual)
      .toLocaleString('pt-BR', { maximumFractionDigits: 1 });

    // No caso normal o sufixo é CURTO ou nenhum: a variação e o valor já ocupam os ~210px do KPI,
    // e as datas exatas estão no `title` de qualquer jeito.
    return `${seta} ${pct}% · ${antes}${ondeCurto}`;
  }

  /** O recorte dos dois períodos, para o tooltip — sempre, não só em andamento. */
  periodoDe(i: IndicadorComparativo): string {
    return `Comparando com ${Relatorios.dia(i.anteriorDe)} – ${Relatorios.dia(i.anteriorAte)}`;
  }

  /** ⚠️ SÓ APARECE COM O PERÍODO EM ANDAMENTO, e tem de aparecer: sem o rótulo, quatro dias contra
   *  trinta pareceriam uma queda de 87% no dia 4 de todo mês. */
  recorte(c: ComparativoVendas): string {
    return `${Relatorios.dia(c.de)}–${Relatorios.dia(c.ate)} vs ` +
           `${Relatorios.dia(c.faturamento.anteriorDe)}–${Relatorios.dia(c.faturamento.anteriorAte)}`;
  }

  // ---------------------------------------------------------------- formato
  moeda(v: number): string {
    return v.toLocaleString('pt-BR', { style: 'currency', currency: 'BRL' });
  }

  /** Só FORMATA: o percentual chega pronto, de 0 a 100 (AUD-XX). Era uma fração de 0 a 1 que a
   *  tela multiplicava por 100 — e "0%" quando não havia nada para medir. */
  pct(v: number | null): string {
    return v === null ? '—' : `${v.toLocaleString('pt-BR', { maximumFractionDigits: 2 })}%`;
  }

  /** Minutos úteis em linguagem de gente. "312 min" não diz nada; "5h12" diz. */
  minutos(v: number): string {
    if (v < 1) return 'menos de 1 min';
    if (v < 60) return `${Math.round(v)} min`;
    const h = Math.floor(v / 60);
    const m = Math.round(v % 60);
    return m === 0 ? `${h}h` : `${h}h${String(m).padStart(2, '0')}`;
  }
}
