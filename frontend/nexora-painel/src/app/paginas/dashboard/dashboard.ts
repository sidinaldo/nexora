import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { RouterLink } from '@angular/router';
import { DashboardServico } from '../../nucleo/servicos/dashboard.servico';
import { MeuDiaServico } from '../../nucleo/servicos/meu-dia.servico';
import { AuthServico } from '../../nucleo/servicos/auth.servico';
import {
  AcaoDoDia, AgrupamentoSerie, Atividade, DashboardDto, EtapaFunilDto, OrigemDto, OrigemLead,
  SerieTemporalDto
} from '../../nucleo/modelos';
import { GraficoLinha, PontoSerie } from '../../nucleo/graficos/grafico-linha';

/** As quatro métricas que a série devolve. */
type Metrica = 'faturamento' | 'leads' | 'vendas' | 'tempo';

/** Uma fatia da rosca, já com o caminho SVG calculado. */
interface FatiaRosca {
  origem: OrigemDto;
  rotulo: string;
  cor: string;
  caminho: string;
  percentual: number;
}

/** O DASHBOARD.
 *
 *  ===================== A SEPARAÇÃO BARATO / CARO =====================
 *  Esta página pede o payload RICO (`/api/dashboard`) UMA VEZ, ao abrir. Ela não faz polling.
 *  Quem faz polling de 45s é o SHELL, e só do `/api/painel/status`, que é barato de propósito.
 *  Colocar o funil e as agregações no poll seria pagar cinco varreduras a cada 45 segundos.
 *  =====================================================================
 *
 *  ===================== NÃO HÁ MAIS MODO DEMONSTRAÇÃO =====================
 *  Havia um alternador aqui que trocava a tela por números gerados (`/api/dashboard/demo`). Ele
 *  resolvia UMA tela e deixava as outras vazias, e os números não passavam por consulta nenhuma
 *  — não provavam que o produto funciona, só que o gerador funcionava.
 *
 *  A demonstração agora é um TENANT com dados de verdade no banco: mesmos serviços, mesmas
 *  consultas, mesmas telas. Ver docs/PI-4b.md.
 *  ========================================================================= */
@Component({
  selector: 'app-dashboard',
  imports: [DatePipe, RouterLink, GraficoLinha],
  templateUrl: './dashboard.html',
  styleUrl: './dashboard.css'
})
export class Dashboard implements OnInit {
  private servico = inject(DashboardServico);
  private meuDia = inject(MeuDiaServico);
  auth = inject(AuthServico);

  // ---- real ----
  dados = signal<DashboardDto | null>(null);
  carregandoNumeros = signal(true);
  erroNumeros = signal('');

  /** O feed real, vindo de /api/dashboard/atividades. Substituiu a lista de conversas: aquilo
   *  era "últimas conversas" chamada de atividade recente — não mostrava venda fechada nem
   *  follow-up concluído, que é metade do que aconteceu no dia. */
  feed = signal<Atividade[]>([]);
  temMaisAtividades = signal(false);
  carregandoAtividades = signal(true);
  erroAtividades = signal('');

  /** TAREFAS PENDENTES — vem do `/api/meu-dia`, o mesmo serviço da tela Meu Dia.
   *
   *  Não é uma consulta nova: o plano do dia JÁ é "o que falta fazer", recortado por
   *  responsável pela API. Inventar um endpoint só para o dashboard duplicaria a regra de quem
   *  vê o quê — e as duas cópias divergiriam na primeira mudança. */
  tarefas = signal<AcaoDoDia[]>([]);
  carregandoTarefas = signal(true);
  erroTarefas = signal('');

  // ---- série real ----
  serieReal = signal<SerieTemporalDto | null>(null);
  carregandoSerie = signal(true);
  erroSerie = signal('');
  /** Dias do período. 365 troca o agrupamento para mês — 365 pontos num gráfico de 1000px é um
   *  ponto a cada 2,7 pixels, ilegível. */
  periodo = signal<30 | 90 | 365>(30);
  metrica = signal<Metrica>('faturamento');

  empresaSemDados = computed(() => {
    const d = this.dados();
    if (!d) return false;
    return d.funil.reduce((s, e) => s + e.contatos, 0) === 0;
  });

  totalNoFunil = computed(() =>
    this.dados()?.funil.reduce((s, e) => s + e.contatos, 0) ?? 0);

  ngOnInit() { this.carregar(); }

  carregar() {
    this.carregandoNumeros.set(true);
    this.erroNumeros.set('');
    this.servico.dashboard().subscribe({
      next: d => { this.dados.set(d); this.carregandoNumeros.set(false); },
      error: () => {
        this.erroNumeros.set('Não foi possível carregar os indicadores.');
        this.carregandoNumeros.set(false);
      }
    });

    // Independentes entre si: uma falhando não derruba as outras. Três cartões, três erros
    // possíveis, cada um avisando no seu lugar em vez de a página inteira virar mensagem de erro.
    this.carregandoAtividades.set(true);
    this.erroAtividades.set('');
    this.servico.atividades(null, null, null, 10).subscribe({
      next: p => {
        this.feed.set(p.itens);
        this.temMaisAtividades.set(p.temMais);
        this.carregandoAtividades.set(false);
      },
      error: () => {
        this.erroAtividades.set('Não foi possível carregar a atividade recente.');
        this.carregandoAtividades.set(false);
      }
    });

    this.carregandoTarefas.set(true);
    this.erroTarefas.set('');
    this.meuDia.meuDia().subscribe({
      next: m => { this.tarefas.set(m.acoes.slice(0, 6)); this.carregandoTarefas.set(false); },
      error: () => {
        this.erroTarefas.set('Não foi possível carregar suas tarefas.');
        this.carregandoTarefas.set(false);
      }
    });

    this.carregarSerie();
  }

  carregarSerie() {
    this.carregandoSerie.set(true);
    this.erroSerie.set('');

    const dias = this.periodo();
    const hoje = new Date();
    const inicio = new Date(hoje);
    inicio.setDate(inicio.getDate() - (dias - 1));

    this.servico.serie(this.iso(inicio), this.iso(hoje), this.agrupamento()).subscribe({
      next: s => { this.serieReal.set(s); this.carregandoSerie.set(false); },
      error: () => {
        this.erroSerie.set('Não foi possível carregar a evolução do período.');
        this.carregandoSerie.set(false);
      }
    });
  }

  trocarPeriodo(dias: 30 | 90 | 365) {
    this.periodo.set(dias);
    this.carregarSerie();
  }

  agrupamento = computed<AgrupamentoSerie>(() => {
    const d = this.periodo();
    return d >= 365 ? 'mes' : d > 60 ? 'semana' : 'dia';
  });

  /** Data local em YYYY-MM-DD. `toISOString()` NÃO serve — mesma armadilha do `chaveDia` do
   *  semáforo: às 21h em Brasília o ISO devolve o dia seguinte, e o período pedido sairia
   *  deslocado em um dia. */
  private iso(d: Date): string {
    const mes = `${d.getMonth() + 1}`.padStart(2, '0');
    const dia = `${d.getDate()}`.padStart(2, '0');
    return `${d.getFullYear()}-${mes}-${dia}`;
  }

  // ================================================================ funil desenhado
  /** O funil DESENHADO como funil: cada faixa é um trapézio que estreita conforme a etapa avança.
   *
   *  A largura é proporcional à PRIMEIRA etapa (o topo é sempre 100%), com piso de 28% — sem o
   *  piso, a última etapa de um funil real vira um fio de 2% e o rótulo não cabe dentro dela.
   *
   *  Proporcional ao TOPO e não ao total: com proporção sobre o total, um funil equilibrado vira
   *  cinco faixas de 20% e o desenho deixa de contar a história da perda ao longo das etapas.
   *
   *  ===================== O QUE FOI CORRIGIDO NO DES-1 =====================
   *  O texto acima descrevia `28 + (contatos / topo) * 72`. Isso é uma função AFIM, não uma
   *  proporção: uma etapa com 3 contatos num funil de 162 desenhava 29% da largura — quase um
   *  terço do topo — enquanto o número ao lado dizia 3. A pessoa lê a barra antes do número, e
   *  a barra mentia. Era exatamente a mistura de "proporcional" com "decorativo".
   *
   *  O piso de 28% existia porque o nome da etapa ficava DENTRO da barra e sumia quando ela era
   *  fina. A correção foi tirar o nome de dentro: ele tem coluna própria à esquerda, e a barra
   *  pode ser tão fina quanto o dado exigir.
   *
   *  A base virou a MAIOR contagem, não a primeira etapa: a de ganho acumula as vendas de todos
   *  os meses e passa o topo do funil com frequência. Com base na primeira, ela estourava os
   *  100% e era cortada pelo teto — outra forma de a barra mentir sobre a proporção.
   *
   *  Decisão registrada em docs/DES-1.md: PROPORCIONAL, não decorativa.
   *  ======================================================================== */
  larguraFaixa(i: number): number {
    const f = this.dados()?.funil ?? [];
    if (f.length === 0) return 0;

    const maior = Math.max(1, ...f.map(e => e.contatos));
    return (f[i].contatos / maior) * 100;
  }

  etapaValor(e: EtapaFunilDto): string {
    return e.valor > 0 ? this.moedaCurta(e.valor) : '';
  }

  /** Degradê verde: mais claro no topo, mais escuro na base.
   *
   *  ===================== DERIVADO DA POSIÇÃO, NÃO DA COR DA ETAPA =====================
   *  A etapa tem `cor` no cadastro, e o kanban a usa. Aqui não: o número de etapas NÃO É FIXO —
   *  a empresa pode ter três ou oito —, e o degradê precisa se distribuir sobre quantas
   *  existirem. Interpolar entre dois tons pelo índice faz isso sozinho; usar a cor configurada
   *  daria um funil de tons aleatórios no dia em que alguém escolhesse rosa para "Proposta".
   *
   *  A troca é consciente: a cor da etapa continua mandando no quadro, onde ela identifica a
   *  coluna. Aqui a forma é uma peça só, e o degradê é o que a faz ler como funil.
   *  ==================================================================================== */
  corDaFaixa(i: number): string {
    const n = Math.max(1, (this.dados()?.funil.length ?? 1) - 1);
    const t = Math.min(1, i / n);

    // #7FBF9B (claro) → #14432F (--verde). Interpolação linear por canal.
    const de = [0x7F, 0xBF, 0x9B];
    const ate = [0x14, 0x43, 0x2F];
    const [r, g, b] = de.map((c, k) => Math.round(c + (ate[k] - c) * t));

    return `rgb(${r}, ${g}, ${b})`;
  }

  // ================================================================ rosca de origens
  /** ===================== A PALETA É SÓ VERDE =====================
   *  A restrição do projeto é verde, creme e UM tom de alerta — e a única exceção acordada são
   *  os três estados do semáforo, onde a cor É a informação. Numa rosca de origens a cor é só
   *  rótulo: qualquer conjunto distinguível serve, e sair da paleta por comodidade é como se
   *  perde a identidade de um produto, um gráfico de cada vez.
   *
   *  Seis tons derivados dos tokens `--verde`, `--verde-2` e `--verde-3`, do mais escuro (a
   *  maior fatia, que vem primeiro) ao mais claro. Passando de seis origens, o excedente vira
   *  "Outros" num tom de creme fechado: sete verdes seguidos deixam de ser distinguíveis, e
   *  legenda que ninguém consegue casar com a fatia não informa nada.
   *  =============================================================== */
  private static readonly TonsVerdes = [
    '#14432F',   // --verde
    '#1D5B3F',   // --verde-2
    '#2E7A56',   // --verde-3
    '#4A9B72',
    '#7FBF9B',
    '#B3DCC6'
  ];

  /** O tom do "Outros": creme fechado, dentro da paleta e claramente fora da série verde. */
  private static readonly TomOutros = '#CFC9B8';

  /** Quantas fatias de verde antes de agrupar o resto. */
  private static readonly MaxFatias = 6;

  private static readonly RotulosOrigem: Record<OrigemLead, string> = {
    instagram: 'Instagram', facebook: 'Facebook', whatsapp: 'WhatsApp', google: 'Google',
    site: 'Site', qrcode: 'QR Code', indicacao: 'Indicação', manual: 'Cadastro manual',
    outro: 'Outro'
  };

  totalOrigens = computed(() =>
    (this.dados()?.origens ?? []).reduce((s, o) => s + o.leads, 0));

  /** A rosca em SVG puro, sem biblioteca — mesmo princípio do grafico-linha.
   *
   *  Cada fatia é um `path` com dois arcos (externo e interno) fechando o anel. O ângulo começa
   *  em -90° para a primeira fatia nascer no topo, que é o que todo mundo espera de uma rosca. */
  /** As origens já agrupadas: as `MaxFatias` maiores, e o resto somado em "Outros".
   *
   *  A API devolve ordenado por volume e NUNCA devolve origem com zero — `GROUP BY` só produz
   *  linha para o que existe. Legenda com sete fatias de zero polui e não informa. */
  private agrupadas = computed(() => {
    const origens = this.dados()?.origens ?? [];
    if (origens.length <= Dashboard.MaxFatias) return origens.map(o => ({ o, agrupado: false }));

    const principais = origens.slice(0, Dashboard.MaxFatias - 1).map(o => ({ o, agrupado: false }));
    const resto = origens.slice(Dashboard.MaxFatias - 1);

    return [
      ...principais,
      {
        o: { origem: 'outro' as OrigemLead, leads: resto.reduce((s, x) => s + x.leads, 0) },
        agrupado: true
      }
    ];
  });

  fatias = computed<FatiaRosca[]>(() => {
    const itens = this.agrupadas();
    const total = this.totalOrigens();
    if (total === 0) return [];

    const cx = 60, cy = 60, rExterno = 54, rInterno = 34;
    let angulo = -Math.PI / 2;

    return itens.map(({ o: origem, agrupado }, indice) => {
      const fracao = origem.leads / total;
      const fatia = fracao * Math.PI * 2;
      const fim = angulo + fatia;
      const maior = fatia > Math.PI ? 1 : 0;

      const p = (r: number, a: number) =>
        `${(cx + r * Math.cos(a)).toFixed(2)},${(cy + r * Math.sin(a)).toFixed(2)}`;

      // Uma origem sozinha fecharia o círculo inteiro, e um arco de 360° com o mesmo ponto de
      // início e fim não desenha nada em SVG. Dois semicírculos resolvem.
      const caminho = fracao >= 0.9999
        ? `M${p(rExterno, -Math.PI / 2)} A${rExterno},${rExterno} 0 1 1 ${p(rExterno, Math.PI / 2)} ` +
          `A${rExterno},${rExterno} 0 1 1 ${p(rExterno, -Math.PI / 2)} ` +
          `M${p(rInterno, -Math.PI / 2)} A${rInterno},${rInterno} 0 1 0 ${p(rInterno, Math.PI / 2)} ` +
          `A${rInterno},${rInterno} 0 1 0 ${p(rInterno, -Math.PI / 2)} Z`
        : `M${p(rExterno, angulo)} ` +
          `A${rExterno},${rExterno} 0 ${maior} 1 ${p(rExterno, fim)} ` +
          `L${p(rInterno, fim)} ` +
          `A${rInterno},${rInterno} 0 ${maior} 0 ${p(rInterno, angulo)} Z`;

      angulo = fim;

      return {
        origem,
        rotulo: agrupado ? 'Outros' : (Dashboard.RotulosOrigem[origem.origem] ?? origem.origem),
        cor: agrupado ? Dashboard.TomOutros : Dashboard.TonsVerdes[indice],
        caminho,
        percentual: fracao
      };
    });
  });

  /** Os percentuais somam exatamente 100%.
   *
   *  Arredondar cada fatia por conta própria dá 99% ou 101% na legenda — o clássico "os números
   *  não fecham" que faz o dono desconfiar do resto da tela. O último recebe a diferença.
   *
   *  O cálculo é do CLIENTE de propósito: no servidor, cada percentual sairia arredondado
   *  isoladamente e o ajuste não teria onde acontecer. */
  percentuaisInteiros = computed<number[]>(() => {
    const fatias = this.fatias();
    if (fatias.length === 0) return [];

    const pcts = fatias.map(f => Math.round(f.percentual * 100));
    const soma = pcts.reduce((s, p) => s + p, 0);
    pcts[pcts.length - 1] += 100 - soma;

    return pcts;
  });

  // ================================================================ gráfico (REAL)
  /** A série no formato do componente de gráfico.
   *
   *  ===================== O TRATAMENTO DO TEMPO DE RESPOSTA =====================
   *  Contagem e dinheiro entram com TODOS os períodos, zeros inclusive: dia sem venda vale zero,
   *  e omiti-lo faria a linha ligar o ponto anterior no seguinte, desenhando subida onde houve
   *  buraco.
   *
   *  Já a média de tempo de resposta OMITE o período sem medição. Não é inconsistência: um dia
   *  em que ninguém escreveu não tem "tempo médio zero" — desenhar zero ali afundaria a linha e
   *  a métrica mostraria seu melhor número justamente quando nada aconteceu.
   *  ============================================================================= */
  serieDoGrafico = computed<PontoSerie[]>(() => {
    const pontos = this.serieReal()?.pontos ?? [];
    const m = this.metrica();

    if (m === 'tempo') {
      return pontos
        .filter(p => p.tempoRespostaMinutos !== null)
        .map(p => ({ data: p.data, valor: p.tempoRespostaMinutos as number }));
    }

    return pontos.map(p => ({
      data: p.data,
      valor: m === 'faturamento' ? p.faturamento : m === 'leads' ? p.leads : p.vendas
    }));
  });

  formatoDoGrafico = computed<'moeda' | 'numero'>(() =>
    this.metrica() === 'faturamento' ? 'moeda' : 'numero');

  /** Quantos períodos ficaram de fora do gráfico de tempo — a tela diz, em vez de esconder. */
  periodosSemMedicao = computed(() => {
    if (this.metrica() !== 'tempo') return 0;
    return (this.serieReal()?.pontos ?? []).filter(p => p.tempoRespostaMinutos === null).length;
  });

  rotuloMetrica = computed(() => {
    switch (this.metrica()) {
      case 'faturamento': return 'Faturamento';
      case 'leads': return 'Leads';
      case 'vendas': return 'Vendas';
      default: return 'Tempo de resposta';
    }
  });

  /** "1h 20min" lê melhor que "80 minutos" quando a espera passa de uma hora. */
  duracao(minutos: number): string {
    const m = Math.round(minutos);
    if (m < 60) return `${m}min`;
    const h = Math.floor(m / 60);
    const resto = m % 60;
    return resto === 0 ? `${h}h` : `${h}h ${resto}min`;
  }

  // ================================================================ atividades (REAL)
  iconeFeed(a: Atividade): string {
    switch (a.tipo) {
      case 'venda': return '✓';
      case 'contato': return '＋';
      case 'lembrete': return '↻';
      default: return '💬';
    }
  }

  // ================================================================ formato
  moeda(v: number): string {
    return v.toLocaleString('pt-BR', { style: 'currency', currency: 'BRL' });
  }

  /** Moeda compacta: "R$ 47,5 mil" cabe onde "R$ 47.500,00" estoura. */
  moedaCurta(v: number): string {
    if (v >= 1_000_000) return `R$ ${(v / 1_000_000).toLocaleString('pt-BR', { maximumFractionDigits: 1 })} mi`;
    if (v >= 10_000) return `R$ ${(v / 1_000).toLocaleString('pt-BR', { maximumFractionDigits: 1 })} mil`;
    return this.moeda(v);
  }

  percentual(fracao: number): string {
    return `${(fracao * 100).toLocaleString('pt-BR', { maximumFractionDigits: 0 })}%`;
  }

  numero(v: number): string { return v.toLocaleString('pt-BR'); }

  iniciais(nome: string): string {
    const p = (nome || '').trim().split(/\s+/);
    return ((p[0]?.[0] ?? '') + (p.length > 1 ? p[p.length - 1][0] : '')).toUpperCase() || '?';
  }
}
