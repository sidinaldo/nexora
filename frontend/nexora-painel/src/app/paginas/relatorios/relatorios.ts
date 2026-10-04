import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { AuthServico } from '../../nucleo/servicos/auth.servico';
import { ToastServico } from '../../nucleo/toast/toast.servico';
import { baixarBlob } from '../../nucleo/download';
import { GraficoBarras, BarraGrafico } from '../../nucleo/graficos/grafico-barras';
import {
  FiltroRelatorio, LinhaCanalVenda, LinhaClienteRecorrente, LinhaMotivoPerda, LinhaOrigem,
  LinhaTempoResposta,
  LinhaVendedor, EntradaEtapa, OpcaoEtapa, OpcoesRelatorio, RelatoriosServico, RelatorioFunil,
  RelatorioVendas
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
  imports: [FormsModule, RouterLink, DatePipe, GraficoBarras],
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
        this.recorrentesTotal.set(p.total);
        this.recorrentesPagina.set(p.numeroPagina);
      },
      error: e => this.falhou(e)
    });
  }

  totalPaginasRecorrentes = computed(() =>
    Math.max(1, Math.ceil(this.recorrentesTotal() / this.porPagina)));

  // ---------------------------------------------------------------- gráficos
  /** A barra clara é o faturamento; a escura, a parte já concluída. Duas barras lado a lado
   *  pediriam ao leitor que somasse mentalmente para saber o total do dia. */
  barrasVendas = computed<BarraGrafico[]>(() =>
    (this.vendas()?.pontos ?? []).map(p => ({
      rotulo: this.rotuloPeriodo(p.periodo),
      valor: p.faturamento,
      destaque: p.valorConcluido
    })));

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

    for (const e of this.funil()?.entradas ?? []) {
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
    Math.max(1, ...(this.funil()?.entradas ?? []).map(e => e.entradas)));

  /** As etapas de AGORA agrupadas, para a tabela. Mesma rotina das entradas. */
  gruposFunilAgora = computed<{ funil: string; etapas: EntradaEtapa[] }[]>(() => {
    const grupos: { funil: string; pipelineId: number; etapas: EntradaEtapa[] }[] = [];

    for (const e of this.funil()?.entradas ?? []) {
      const ultimo = grupos[grupos.length - 1];
      if (ultimo && ultimo.pipelineId === e.pipelineId) ultimo.etapas.push(e);
      else grupos.push({ funil: e.pipelineNome, pipelineId: e.pipelineId, etapas: [e] });
    }

    return grupos;
  });

  /** A foto da etapa, para a coluna ao lado das entradas. Um `find` sobre no máximo meia dúzia
   *  de etapas — não vale um Map, e um pipe só para isto seria mais peça para manter. */
  agoraDa(etapaId: number) {
    return this.funil()?.agora.find(a => a.etapaId === etapaId) ?? null;
  }

  private rotuloPeriodo(iso: string): string {
    const d = new Date(iso + 'T00:00:00');
    return this.agrupamento() === 'mes'
      ? d.toLocaleDateString('pt-BR', { month: 'short', year: '2-digit' })
      : d.toLocaleDateString('pt-BR', { day: '2-digit', month: '2-digit' });
  }

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

  // ---------------------------------------------------------------- formato
  moeda(v: number): string {
    return v.toLocaleString('pt-BR', { style: 'currency', currency: 'BRL' });
  }

  pct(v: number): string {
    return `${(v * 100).toLocaleString('pt-BR', { maximumFractionDigits: 1 })}%`;
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
