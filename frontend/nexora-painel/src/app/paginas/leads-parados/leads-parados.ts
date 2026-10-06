import { Component, ElementRef, OnInit, ViewChild, computed, inject, signal } from '@angular/core';
import { DecimalPipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Paginacao, alturaMinimaDaTabela, rolarParaTopoDaTabela, totalDePaginas }
  from '../../nucleo/paginacao/paginacao';
import { AuthServico } from '../../nucleo/servicos/auth.servico';
import { EtiquetaNaLista } from '../../nucleo/modelos';
import { chaveDia } from '../../nucleo/semaforo';
import { EtiquetasServico } from '../../nucleo/servicos/etiquetas.servico';
import {
  FiltroLeadsParados, JanelaDeParada, LeadParado, LeadsParadosServico, ResultadoEmLote
} from '../../nucleo/servicos/leads-parados.servico';
import {
  OpcaoEtapa, OpcoesRelatorio, RelatoriosServico
} from '../../nucleo/servicos/relatorios.servico';

/** ===================== LEADS PARADOS (LPA-1) =====================
 *
 *  O lead que chegou, teve uma conversa e esfriou fica no funil para sempre, indistinguível do que
 *  foi atendido ontem. Até aqui, descobrir isso exigia abrir contato por contato.
 *
 *  ⚠️ VER E AGIR SÃO SEPARADOS. A rota não tem guarda — o vendedor abre e vê a lista de trabalho
 *  dele. Agir sobre vários de uma vez exige `agir_em_lote`, e sem o gesto a coluna de seleção nem
 *  renderiza: oferecer a caixinha e recusar no fim seria pior que não oferecer.
 *
 *  ⚠️ O RECORTE POR PESSOA NÃO ESTÁ AQUI. Quem não tem `ver_numeros_da_equipe` recebe só os
 *  próprios leads, decidido no servidor. A tela não esconde nada: ela não recebe. É por isso que a
 *  rota não tem guarda — o vendedor abre e vê a lista de trabalho dele.
 *
 *  O seletor de responsável vem travado para o vendedor porque `/relatorios/opcoes` devolve só ele
 *  — não porque a tela decide.
 *  ============================================================== */
@Component({
  selector: 'app-leads-parados',
  imports: [FormsModule, RouterLink, DecimalPipe, Paginacao],
  templateUrl: './leads-parados.html',
  styleUrl: './leads-parados.css'
})
export class LeadsParados implements OnInit {
  private api = inject(LeadsParadosServico);
  private relatorios = inject(RelatoriosServico);
  private etiquetasApi = inject(EtiquetasServico);
  auth = inject(AuthServico);

  readonly janelas = this.api.janelas;
  readonly tamanho = this.api.porPagina;

  @ViewChild('tabelaTopo') private tabelaTopo?: ElementRef<HTMLElement>;

  dias = signal<JanelaDeParada>(30);
  responsavelId = signal<number | null>(null);
  pipelineId = signal<number | null>(null);
  etapaId = signal<number | null>(null);
  origem = signal<string | null>(null);
  etiquetaId = signal<number | null>(null);
  valorMin = signal<number | null>(null);
  valorMax = signal<number | null>(null);

  /** Os filtros secundários começam FECHADOS. Seis controles abertos de uma vez transformam a
   *  tela num formulário, e a pergunta dela é "quem parou", não "monte uma consulta". */
  maisFiltros = signal(false);

  etiquetas = signal<EtiquetaNaLista[]>([]);

  /** As origens são enum FECHADO no servidor, então a lista pode viver aqui sem risco de
   *  divergir: valor inventado é recusado com 400, não ignorado em silêncio. Mesma lista e mesma
   *  razão de `relatorios.ts`. */
  readonly origens = [
    'instagram', 'facebook', 'whatsapp', 'google', 'site', 'qrcode', 'indicacao', 'meta_ads',
    'manual', 'outro'
  ];

  /** ===================== A SELEÇÃO É POR CONTATO, NÃO POR LINHA =====================
   *
   *  ⚠️ A tabela mostra uma linha por NEGOCIAÇÃO aberta: quem tem negócio em dois funis aparece
   *  duas vezes. O lembrete é do CONTATO — ele recebe uma tarefa, não duas. Guardar `contatoId`
   *  faz as duas linhas da mesma pessoa marcarem juntas, que é o que de fato vai acontecer, e o
   *  contador diz "contatos" em vez de prometer um número que o servidor vai deduplicar.
   *  ================================================================================== */
  selecionados = signal<ReadonlySet<number>>(new Set());

  loteAberto = signal(false);
  loteTitulo = signal('Retomar contato');
  loteData = signal('');
  loteObs = signal('');
  salvandoLote = signal(false);
  erroLote = signal('');

  /** Fica na tela DEPOIS de fechar o modal: o resultado tem um número que precisa ser lido
   *  ("pulados"), e um aviso que morre com o modal não é lido. */
  resultadoLote = signal<ResultadoEmLote | null>(null);

  itens = signal<LeadParado[]>([]);
  total = signal(0);
  pagina = signal(1);
  carregando = signal(false);
  erro = signal('');

  opcoes = signal<OpcoesRelatorio>({ responsaveis: [], etapas: [], motivosPerda: [] });

  totalPaginas = computed(() => totalDePaginas(this.total(), this.tamanho));

  /** Reserva a altura para o rodapé de paginação não pular entre uma página cheia e a última. */
  alturaMinima = computed(() =>
    this.totalPaginas() > 1 ? alturaMinimaDaTabela(this.tamanho) : 0);

  /** O recorte inteiro, num lugar só. Montá-lo dentro do `carregar()` faria cada filtro novo
   *  exigir uma linha a mais lá dentro — e esquecer uma é um filtro que a tela mostra e não
   *  aplica, que é o defeito mais silencioso que uma barra de filtros pode ter. */
  filtro = computed<FiltroLeadsParados>(() => ({
    dias: this.dias(),
    pagina: this.pagina(),
    responsavelId: this.responsavelId(),
    pipelineId: this.pipelineId(),
    etapaId: this.etapaId(),
    origem: this.origem(),
    etiquetaId: this.etiquetaId(),
    valorMin: this.valorMin(),
    valorMax: this.valorMax()
  }));

  /** O texto do vazio é BIFURCADO: "ninguém parado" e "ninguém parado com esse recorte" mandam a
   *  pessoa fazer coisas diferentes. Dizer só o primeiro com um filtro ligado esconde o filtro. */
  temFiltro = computed(() =>
    this.dias() !== 30
    || this.responsavelId() !== null
    || this.pipelineId() !== null
    || this.etapaId() !== null
    || this.origem() !== null
    || this.etiquetaId() !== null
    || this.valorMin() !== null
    || this.valorMax() !== null);

  /** ⚠️ FILTRO DE NEGOCIAÇÃO ESCONDE QUEM NÃO TEM NEGÓCIO, e é o certo: o lead que ninguém abriu
   *  não está em funil nenhum, não tem etapa, valor nem etiqueta de negociação. Mas a lista
   *  encolhe sem explicação, e o operador conclui que esses leads sumiram do sistema. A tela diz. */
  escondeSemNegocio = computed(() =>
    this.pipelineId() !== null || this.etapaId() !== null
    || this.etiquetaId() !== null || this.valorMin() !== null || this.valorMax() !== null);

  /** As etapas agrupadas por funil, NA ORDEM EM QUE A API MANDOU.
   *
   *  Percorre em sequência e quebra quando o funil muda — não agrupa por `Map`. A API já devolve
   *  ordenado por funil e depois por etapa; reagrupar por chave jogaria essa ordem fora e as
   *  etapas sairiam embaralhadas dentro do próprio grupo. Mesma solução de `relatorios.ts`. */
  etapasPorFunil = computed(() => {
    const grupos: { funil: string; etapas: OpcaoEtapa[] }[] = [];

    for (const e of this.opcoes().etapas) {
      const ultimo = grupos[grupos.length - 1];
      if (ultimo && ultimo.etapas[0].pipelineId === e.pipelineId) ultimo.etapas.push(e);
      else grupos.push({ funil: e.pipelineNome, etapas: [e] });
    }

    return grupos;
  });

  /** Os funis, deduzidos das etapas: `/opcoes` não devolve lista de funis própria, e um endpoint
   *  novo para isso seria uma segunda fonte da mesma informação. */
  funis = computed(() =>
    this.etapasPorFunil().map(g => ({ id: g.etapas[0].pipelineId, nome: g.funil })));

  /** Sem o gesto a coluna nem aparece. Quem decide é o servidor — `Exigir` na ação —, e isto
   *  aqui só evita oferecer um caminho que termina em recusa. */
  podeAgirEmLote = computed(() => this.auth.pode('agir_em_lote'));

  /** Contatos distintos na página: é o universo do "marcar todos", e não o número de linhas. */
  contatosNaPagina = computed(() => [...new Set(this.itens().map(l => l.contatoId))]);

  quantosMarcados = computed(() => this.selecionados().size);

  todosMarcados = computed(() =>
    this.contatosNaPagina().length > 0
    && this.contatosNaPagina().every(id => this.selecionados().has(id)));

  ngOnInit() {
    // ⚠️ REAPROVEITA `/relatorios/opcoes`, que já devolve a lista de responsáveis RECORTADA pelo
    // papel de quem pergunta. Um endpoint novo seria uma segunda fonte da mesma lista, e o dia em
    // que o recorte mudasse num só deixaria a outra tela oferecendo gente que ela não pode ver.
    this.relatorios.opcoes().subscribe({
      next: o => this.opcoes.set(o),
      // Seletor vazio é menos ruim que a tela não abrir: a lista não depende dele para funcionar.
      error: () => { }
    });

    // Listar etiqueta é de qualquer papel. Falha aqui deixa o seletor vazio, nunca impede a lista
    // de abrir — a tela não depende dele para funcionar.
    this.etiquetasApi.listar().subscribe({
      next: e => this.etiquetas.set(e),
      error: () => { }
    });

    this.carregar();
  }

  carregar() {
    this.carregando.set(true);

    // ⚠️ TROCAR DE PÁGINA OU DE FILTRO APAGA A SELEÇÃO. Mantê-la daria uma seleção invisível —
    // trinta marcados na página 1 que o operador não vê mais e não lembra — e somada à página 2
    // passaria do teto do servidor, voltando 400 no fim de um trabalho já feito.
    this.selecionados.set(new Set());

    this.api.listar(this.filtro()).subscribe({
      next: p => {
        this.itens.set(p.itens);
        this.total.set(p.total);
        this.carregando.set(false);
        this.erro.set('');
      },
      error: () => {
        this.erro.set('Não foi possível carregar os leads parados. Tente de novo.');
        this.carregando.set(false);
      }
    });
  }

  /** Todo filtro volta para a página 1: continuar na página 7 de um recorte que agora tem duas
   *  páginas devolveria uma lista vazia que parece "não há nada". */
  private doZero() {
    this.pagina.set(1);
    this.carregar();
  }

  trocarJanela(dias: JanelaDeParada) {
    if (dias === this.dias()) return;

    this.dias.set(dias);
    this.doZero();
  }

  /** Um método para todos os seletores: um por filtro seria a mesma linha seis vezes, e a sexta é
   *  a que alguém esquece de fechar com `doZero()` — a tela volta na página 7 de um recorte que
   *  agora tem duas, e o vazio diz "nenhum lead parado". */
  trocarSeletor(qual: 'responsavel' | 'funil' | 'etapa' | 'origem' | 'etiqueta', valor: string) {
    const id = valor ? Number(valor) : null;

    if (qual === 'responsavel') this.responsavelId.set(id);
    else if (qual === 'funil') {
      this.pipelineId.set(id);
      // ⚠️ TROCAR O FUNIL LIMPA A ETAPA. A etapa pertence a um funil; mantê-la daria um recorte
      // que nunca casa — funil A com etapa do funil B — e a lista viria vazia sem dizer por quê.
      this.etapaId.set(null);
    }
    else if (qual === 'etapa') this.etapaId.set(id);
    else if (qual === 'etiqueta') this.etiquetaId.set(id);
    else this.origem.set(valor || null);

    this.doZero();
  }

  trocarValor(qual: 'min' | 'max', valor: string) {
    const n = valor.trim() === '' ? null : Number(valor);
    const limpo = n === null || Number.isNaN(n) ? null : n;

    if (qual === 'min') this.valorMin.set(limpo);
    else this.valorMax.set(limpo);

    this.doZero();
  }

  limpar() {
    this.dias.set(30);
    this.responsavelId.set(null);
    this.pipelineId.set(null);
    this.etapaId.set(null);
    this.origem.set(null);
    this.etiquetaId.set(null);
    this.valorMin.set(null);
    this.valorMax.set(null);
    this.doZero();
  }

  irPara(p: number) {
    if (p < 1 || p > this.totalPaginas()) return;

    this.pagina.set(p);
    this.carregar();
    rolarParaTopoDaTabela(this.tabelaTopo?.nativeElement);
  }

  // ================================================================ a seleção e o lote

  marcado(contatoId: number): boolean {
    return this.selecionados().has(contatoId);
  }

  alternar(contatoId: number) {
    const novo = new Set(this.selecionados());

    if (!novo.delete(contatoId)) novo.add(contatoId);
    this.selecionados.set(novo);
  }

  desmarcarTudo() {
    this.selecionados.set(new Set());
  }

  /** Marca ou desmarca a página inteira. "Tudo" é a PÁGINA, nunca o resultado todo: o servidor
   *  aceita no máximo uma página por chamada, e um botão que marcasse 300 leads prometeria uma
   *  ação que volta 400. */
  alternarTodos() {
    this.selecionados.set(this.todosMarcados() ? new Set() : new Set(this.contatosNaPagina()));
  }

  abrirLote() {
    if (this.quantosMarcados() === 0) return;

    this.erroLote.set('');
    this.resultadoLote.set(null);
    this.loteTitulo.set('Retomar contato');
    this.loteObs.set('');
    this.loteData.set(this.emDias(1));
    this.loteAberto.set(true);
  }

  fecharLote() {
    if (this.salvandoLote()) return;

    this.loteAberto.set(false);
  }

  confirmarLote() {
    const titulo = this.loteTitulo().trim();

    if (titulo === '' || this.loteData() === '' || this.quantosMarcados() === 0) return;

    this.salvandoLote.set(true);
    this.erroLote.set('');

    this.api.criarLembretes({
      contatoIds: [...this.selecionados()],
      dataAlvo: this.loteData(),
      titulo,
      observacao: this.loteObs().trim() || null
    }).subscribe({
      next: r => {
        this.salvandoLote.set(false);
        this.loteAberto.set(false);
        // Recarrega ANTES de guardar o resultado: `carregar()` apaga a seleção, e o aviso tem de
        // sobreviver a isso — é a única coisa na tela que diz quantos foram pulados.
        this.carregar();
        this.resultadoLote.set(r);
      },
      error: () => {
        this.salvandoLote.set(false);
        this.erroLote.set('Não foi possível criar os lembretes. Tente de novo.');
      }
    });
  }

  /** A data em `aaaa-mm-dd` pelo relógio LOCAL.
   *
   *  ⚠️ USA `chaveDia`, NÃO UMA SEGUNDA CÓPIA. Ela já existe para a mesma regra — `toISOString()`
   *  devolve a data em UTC e às 21h em Brasília já virou o dia —, e `semaforo.spec.ts` a guarda
   *  com um `Date` falso que morde até num runner em UTC. Uma cópia aqui ficaria sem essa guarda. */
  private emDias(n: number): string {
    const d = new Date();

    d.setDate(d.getDate() + n);

    return chaveDia(d);
  }

  /** "há 3 meses" responde a pergunta; "há 97 dias" é preciso e não responde. */
  tempoParado(dias: number): string {
    if (dias < 1) return 'hoje';
    if (dias === 1) return 'ontem';
    if (dias < 30) return `há ${dias} dias`;

    const meses = Math.floor(dias / 30);

    return meses === 1 ? 'há 1 mês' : `há ${meses} meses`;
  }
}
