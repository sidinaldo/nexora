import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { RouterLink } from '@angular/router';
import { DashboardServico } from '../../nucleo/servicos/dashboard.servico';
import { MeuDiaServico } from '../../nucleo/servicos/meu-dia.servico';
import { AuthServico } from '../../nucleo/servicos/auth.servico';
import { PainelServico } from '../../nucleo/servicos/painel.servico';
import {
  AcaoDoDia, AgrupamentoSerie, Atividade, DashboardDto, FatiaOrigemDto, FunilNoPainelDto,
  SerieTemporalDto
} from '../../nucleo/modelos';
import { GraficoLinha, PontoSerie } from '../../nucleo/graficos/grafico-linha';
import { iniciais } from '../../nucleo/iniciais';
import { ROTULO_ORIGEM } from '../../nucleo/rotulos';

/** As quatro métricas que a série devolve. */
type Metrica = 'faturamento' | 'leads' | 'vendas' | 'tempo';

/** Uma fatia da rosca, já com o caminho SVG calculado.
 *
 *  ===================== A FATIA É A ORIGEM, NÃO A CAMPANHA =====================
 *  "Promoção de Julho" é um link de WhatsApp distribuído NO Instagram: a origem é Instagram, e a
 *  campanha é a peça dentro dela. São hierarquia, não alternativas — o próprio modelo diz isso,
 *  `canais_captacao.origem` é escolhida ao criar o canal e o contato herda dela.
 *
 *  ⚠️ Minha primeira versão trocou o rótulo da fatia pelo nome da campanha, e aquilo ACHATAVA a
 *  hierarquia: com duas campanhas no Instagram, a rosca mostraria duas fatias e o dono perderia
 *  o "quanto o Instagram me traz" — que é a pergunta que uma rosca de origens existe para
 *  responder. A campanha desceu para sub-linha da legenda, onde detalha sem competir.
 *  ============================================================================== */
interface FatiaRosca {
  origem: FatiaOrigemDto;
  rotulo: string;
  cor: string;
  caminho: string;
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
  private painel = inject(PainelServico);
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
  carregandoMaisAtividades = signal(false);

  /** Quantas atividades o cartão pede por vez. Dez preenche o cartão sem alongar a página; o
   *  resto vem por "Carregar mais". */
  readonly porPagina = 10;
  carregandoAtividades = signal(true);
  erroAtividades = signal('');

  /** TAREFAS PENDENTES — vem do `/api/meu-dia`, o mesmo serviço da tela Meu Dia.
   *
   *  Não é uma consulta nova: o plano do dia JÁ é "o que falta fazer", recortado por
   *  responsável pela API. Inventar um endpoint só para o dashboard duplicaria a regra de quem
   *  vê o quê — e as duas cópias divergiriam na primeira mudança. */
  tarefas = signal<AcaoDoDia[]>([]);

  /** Quantas tarefas cabem no cartão. Vai como `limite` para a API — o corte acontece no SQL,
   *  não aqui. */
  readonly tarefasNoCartao = 6;

  /** O TOTAL de pendências, que é maior que `tarefas().length` quando o teto cortou. Vem dos
   *  contadores da resposta, sem uma segunda chamada. */
  totalTarefas = signal(0);
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

  /** ===================== QUADRO VAZIO NÃO É EMPRESA NOVA (POS-1) =====================
   *  Chamava-se `empresaSemDados` e somava os cards do quadro — e o nome é a causa do bug que
   *  isto conserta: dizia "sem dados" e significava "sem cards".
   *
   *  Card sai do quadro quando o pedido é concluído (`RegrasNegociacao.NoQuadro` só admite aberta
   *  e ganha). Então a empresa que VENDEU TUDO e concluiu tudo tem o quadro vazio, e a tela a
   *  tratava como recém-criada: aviso de boas-vindas pedindo para cadastrar um contato, numa conta
   *  com duas vendas no mês.
   *
   *  ⚠️ E O AVISO SUBSTITUI A PÁGINA INTEIRA. O defeito não era só o texto errado — eram os
   *  números reais (as vendas, o faturamento, o gráfico, o feed) escondidos atrás dele. Por isso a
   *  condição é "nunca teve nada", e por isso é um `&&`: estreia só quando não há literalmente
   *  nada para mostrar embaixo.
   *
   *  Os dois sinais vêm do servidor, do mesmo `/api/dashboard` que já carrega esta tela. A tela
   *  não consegue derivá-los: "já recebeu mensagem alguma vez" não está em nenhum número daqui.
   *  ================================================================================ */
  empresaEstreando = computed(() => {
    const d = this.dados();
    if (!d) return false;
    return !d.recebeuMensagem && !d.temContato;
  });

  /** ===================== SEM CONTATO NÃO É SEM CONEXÃO =====================
   *  O vazio acontece nos DOIS lados do
   *  onboarding: antes de conectar o WhatsApp e depois de conectar, enquanto a primeira
   *  mensagem não chega. Tratar os dois como um só fazia a tela mandar conectar um número
   *  que já estava no ar, que é pedir para a pessoa refazer o que acabou de fazer.
   *
   *  Vem do status que o SHELL já busca (`PainelServico.ultimo`), não de uma chamada nova: o
   *  mesmo fato, uma requisição só.
   *
   *  Três estados, e o `null` importa: antes da primeira resposta não dá para afirmar nem
   *  "conecte" nem "está conectado", então a tela não afirma nenhum dos dois. */
  whatsappConectado = computed<boolean | null>(() =>
    this.painel.ultimo()?.whatsappConectado ?? null);

  // ⚠️ A LINHA "TODOS" NÃO É MAIS SOMADA AQUI (AUD-XX): `totalEmNegociacao` e `totalValorEmAberto`
  // chegam prontos no `DashboardDto`.

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
    this.servico.atividades(null, null, null, this.porPagina).subscribe({
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
    // O LIMITE VAI PARA A API. Antes era `.slice(0, 6)` aqui: a resposta trazia toda conversa
    // esperando e todo lembrete pendente, e o navegador jogava fora o que não cabia.
    this.meuDia.meuDia(this.tarefasNoCartao).subscribe({
      next: m => {
        this.tarefas.set(m.acoes);
        // Os contadores são o TOTAL, não o tamanho da lista — é o que permite "6 de 23".
        this.totalTarefas.set(m.respondendo + m.lembretes);
        this.carregandoTarefas.set(false);
      },
      error: () => {
        this.erroTarefas.set('Não foi possível carregar suas tarefas.');
        this.carregandoTarefas.set(false);
      }
    });

    this.carregarSerie();
  }

  /** ===================== MAIS ATIVIDADES, POR CURSOR =====================
   *  O cursor é o par `(quando, chave)` do ÚLTIMO item — a mesma ordenação que o serviço usa. Não
   *  é offset de propósito: o feed recebe evento novo o tempo todo, e com offset a segunda página
   *  repetiria ou pularia item conforme o topo crescesse.
   *
   *  ACRESCENTA ao fim. Substituir a lista faria o cartão "avançar" em vez de crescer, e o
   *  vendedor perderia o que estava lendo.
   *
   *  Este cartão pagina — e o de tarefas não — porque o feed NÃO TEM tela de destino: "Abrir
   *  caixa" leva às conversas, que é outra coisa. As tarefas têm o Meu Dia a um clique.
   *  ====================================================================== */
  carregarMaisAtividades() {
    const ultimo = this.feed().at(-1);
    if (!ultimo || this.carregandoMaisAtividades()) return;

    this.carregandoMaisAtividades.set(true);
    this.servico.atividades(ultimo.quando, ultimo.chave, null, this.porPagina).subscribe({
      next: p => {
        this.feed.update(lista => [...lista, ...p.itens]);
        this.temMaisAtividades.set(p.temMais);
        this.carregandoMaisAtividades.set(false);
      },
      error: () => {
        this.carregandoMaisAtividades.set(false);
        this.erroAtividades.set('Não foi possível carregar mais atividades.');
      }
    });
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

  /** O valor da linha, curto. "R$ 1.240.000,00" por extenso empurraria a conversao para fora da
   *  tabela em tela estreita; o travessao diz "nenhum valor" sem fingir que e zero reais. */
  valorDoFunil(f: FunilNoPainelDto): string {
    return f.valorEmAberto > 0 ? this.moedaCurta(f.valorEmAberto) : '—';
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

  // ⚠️ O MAPA DE RÓTULOS SAIU DAQUI para `nucleo/rotulos.ts`: a tela de importar precisa do mesmo,
  // e copiá-lo faria a origem nova (`meta_ads`) aparecer com nome bonito num lugar e crua no
  // outro — que foi o que aconteceu quando ela entrou no servidor.

  /** NEG-3 · o ranking de campanhas do mês. Vem pronto do servidor — três linhas no máximo. */
  campanhas = computed(() => this.dados()?.campanhas ?? []);

  // ===================== A ROSCA CHEGA AGRUPADA (AUD-XX) =====================
  // Somar por origem, cortar as seis maiores, juntar o resto em "Outros" e ajustar os percentuais
  // para fechar 100 era trabalho DESTA tela. Passou para o servidor (`ServicoDashboard.Rosca`): o
  // que sobra aqui é desenhar, pintar e rotular.
  // =========================================================================

  fatias = computed<FatiaRosca[]>(() => {
    const itens = this.dados()?.origens ?? [];
    if (itens.length === 0) return [];

    const cx = 60, cy = 60, rExterno = 54, rInterno = 34;
    let angulo = -Math.PI / 2;

    return itens.map((origem, indice) => {
      // O tamanho do arco vem do percentual do SERVIDOR: é geometria a partir de um número pronto.
      const fracao = origem.percentual / 100;
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
        // O rótulo da FATIA é a origem — ver `FatiaRosca`. As campanhas descem para a legenda.
        rotulo: origem.agrupada ? 'Outros' : (ROTULO_ORIGEM[origem.origem as keyof typeof ROTULO_ORIGEM] ?? origem.origem),
        cor: origem.agrupada ? Dashboard.TomOutros : Dashboard.TonsVerdes[indice],
        caminho
      };
    });
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

  /** Um percentual que JÁ VEIO PRONTO, de 0 a 100 (AUD-XX). Era `fracao * 100` aqui. Null — nada
   *  para medir — vira travessão, e não "0%". */
  pct(v: number | null): string {
    return v === null ? '—' : `${v.toLocaleString('pt-BR', { maximumFractionDigits: 2 })}%`;
  }

  numero(v: number): string { return v.toLocaleString('pt-BR'); }

  /** Uma copia so, em `nucleo/iniciais.ts` — o avatar e a MESMA coisa em toda tela. Eram seis
   *  copias, e as de contato mostravam "(9" para quem nasceu com o telefone por nome. */
  protected readonly iniciais = iniciais;
}
