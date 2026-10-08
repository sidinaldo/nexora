import { Component, ElementRef, OnInit, ViewChild, computed, inject, signal } from '@angular/core';
import { DecimalPipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Paginacao, alturaMinimaDaTabela, rolarParaTopoDaTabela }
  from '../../nucleo/paginacao/paginacao';
import { AuthServico } from '../../nucleo/servicos/auth.servico';
import { EtiquetaNaLista } from '../../nucleo/modelos';
import { chaveDia } from '../../nucleo/semaforo';
import { EtiquetasServico } from '../../nucleo/servicos/etiquetas.servico';
import { ToastServico } from '../../nucleo/toast/toast.servico';
import { textoSobre } from '../../nucleo/cor';
import {
  AbaDeLeads, FiltroLeadsParados, JanelaDeParada, LeadParado, LeadsParadosServico, Reativacao,
  ResultadoEmLote
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
  private toast = inject(ToastServico);

  /** O contraste do texto sobre a cor do chip — o mesmo cálculo da ficha do contato. */
  readonly textoSobre = textoSobre;
  auth = inject(AuthServico);

  readonly janelas = this.api.janelas;
  readonly tamanho = this.api.porPagina;

  @ViewChild('tabelaTopo') private tabelaTopo?: ElementRef<HTMLElement>;

  /** ⚠️ A ABA É O PRIMEIRO RECORTE, e trocar de aba zera a seleção: os leads marcados em
   *  "parados" não existem na outra lista, e levar a seleção faria o botão de reabrir agir sobre
   *  gente que saiu da tela. `carregar()` apaga a seleção em toda troca, e isto é uma delas. */
  aba = signal<AbaDeLeads>('parados');

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

  /** ===================== A SELEÇÃO É POR LINHA, E AS DUAS AÇÕES A LEEM DIFERENTE =====================
   *
   *  A linha é a NEGOCIAÇÃO aberta: quem tem negócio em dois funis aparece duas vezes, e os dois
   *  podem estar parados por motivos diferentes. A chave é a mesma do `track` do template —
   *  `negociacaoId`, ou `-contatoId` para o lead que ninguém abriu.
   *
   *  ⚠️ FOI POR CONTATO E TEVE DE MUDAR. Marcar a pessoa obrigaria a etiqueta a cair nos DOIS
   *  negócios dela, e aí o negócio que não estava sendo reativado seria creditado à reativação
   *  quando fosse ganho — exatamente o que a etiqueta de negociação (e não de contato) existe
   *  para evitar. Então a seleção segue a granularidade da TABELA, e cada ação a reduz:
   *
   *    · LEMBRETE  → contatos distintos. A pessoa recebe UMA tarefa, não duas;
   *    · ETIQUETA  → as negociações marcadas. Linha sem negócio aberto não tem onde colar.
   *  ================================================================================================= */
  selecionados = signal<ReadonlySet<number>>(new Set());

  /** Qual ação o modal está pedindo. Um modal para as duas, porque as duas têm o mesmo formato —
   *  confirmar sobre a seleção e mostrar o resultado —, e dois modais quase iguais divergem. */
  loteAberto = signal<'lembrete' | 'etiqueta' | 'responsavel' | null>(null);

  /** A etiqueta a colar. Começa vazia DE PROPÓSITO: pré-escolher a primeira da lista faria o
   *  operador aplicar "cliente vip" em cinquenta cards por ter clicado rápido. */
  loteEtiqueta = signal<number | null>(null);

  /** ===================== O ALVO DA REDISTRIBUICAO =====================
   *
   *  ⚠️ TRES ESTADOS, NAO DOIS: ninguém escolhido ainda (`undefined`), alguém (`number`) e
   *  "sem responsável" (`null`). Com só dois, "deixar sem dono" — que é metade do uso real, tirar
   *  o lead de quem saiu de férias — seria indistinguível de "ainda não escolhi", e o botão teria
   *  de ficar habilitado desde o começo.
   *  ==================================================================== */
  loteResponsavel = signal<number | null | undefined>(undefined);

  loteTitulo = signal('Retomar contato');
  loteData = signal('');
  loteObs = signal('');
  salvandoLote = signal(false);
  erroLote = signal('');

  /** Fica na tela DEPOIS de fechar o modal: o resultado tem um número que precisa ser lido
   *  ("pulados"), e um aviso que morre com o modal não é lido. */
  resultadoLote = signal<ResultadoEmLote | null>(null);

  /** ===================== QUAL ACAO PRODUZIU O AVISO =====================
   *
   *  ⚠️ CONSERTA UM DEFEITO QUE EU MESMO COMITEI. O aviso escolhia a frase pela ABA, e quatro
   *  ações caem nele — então aplicar etiqueta escrevia "2 lembretes criados", que é simplesmente
   *  falso. O teste da entrega anterior não pegou porque conferia só o caminho do lembrete.
   *
   *  E "pulados" tem motivo diferente em cada uma: já era dessa pessoa · já tinha a etiqueta ·
   *  não tem funil livre · já tinha lembrete pendente. Um número sem a causa certa manda o
   *  operador procurar o problema errado.
   *  ====================================================================== */
  ultimaAcao = signal<'lembrete' | 'etiqueta' | 'responsavel' | 'reabrir' | null>(null);

  /** ===================== O QUE A REATIVACAO RENDEU =====================
   *
   *  Fica NESTA tela, e não em `/relatorios`, porque é aqui que a etiqueta é colada: o laço
   *  fecha no mesmo lugar onde o trabalho é feito.
   *
   *  ⚠️ COMEÇA FECHADO. A pergunta do dia a dia é "quem parou"; "o que a campanha rendeu" é de
   *  outro momento, e um bloco de números no topo empurraria a lista para baixo todo dia. */
  metricaAberta = signal(false);
  metricaEtiqueta = signal<number | null>(null);
  metricaDe = signal('');
  metricaAte = signal('');
  metrica = signal<Reativacao | null>(null);
  carregandoMetrica = signal(false);
  erroMetrica = signal('');

  itens = signal<LeadParado[]>([]);
  total = signal(0);
  pagina = signal(1);
  carregando = signal(false);
  erro = signal('');

  opcoes = signal<OpcoesRelatorio>({ responsaveis: [], etapas: [], motivosPerda: [] });

  /** Quantas páginas há, do servidor (AUD-XX, #21). A tela dividia o total pelo tamanho. */
  totalPaginas = signal(1);

  /** Reserva a altura para o rodapé de paginação não pular entre uma página cheia e a última. */
  alturaMinima = computed(() =>
    this.totalPaginas() > 1 ? alturaMinimaDaTabela(this.tamanho) : 0);

  /** O recorte inteiro, num lugar só. Montá-lo dentro do `carregar()` faria cada filtro novo
   *  exigir uma linha a mais lá dentro — e esquecer uma é um filtro que a tela mostra e não
   *  aplica, que é o defeito mais silencioso que uma barra de filtros pode ter. */
  filtro = computed<FiltroLeadsParados>(() => ({
    aba: this.aba(),
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

  /** A chave de uma linha. ⚠️ A MESMA EXPRESSÃO DO `track` NO TEMPLATE: se as duas divergirem, a
   *  caixinha marca uma linha e o Angular redesenha outra. `negociacaoId` é positivo e o contato
   *  sem negócio entra negativo, então as duas faixas nunca colidem. */
  chave(l: LeadParado): number {
    return l.negociacaoId ?? -l.contatoId;
  }

  linhasDaPagina = computed(() => this.itens().map(l => this.chave(l)));

  quantosMarcados = computed(() => this.selecionados().size);

  /** As negociações marcadas: as chaves POSITIVAS. A negativa é lead sem negócio aberto — ele
   *  conta na seleção, recebe lembrete, e não tem onde colar etiqueta. */
  negociacoesMarcadas = computed(() => [...this.selecionados()].filter(k => k > 0));

  /** Os contatos marcados, SEM REPETIR: duas linhas da mesma pessoa viram um lembrete. */
  contatosMarcados = computed(() => [...new Set(
    this.itens().filter(l => this.selecionados().has(this.chave(l))).map(l => l.contatoId))]);

  /** Quantos dos marcados não têm negócio aberto. A tela DIZ esse número antes de aplicar
   *  etiqueta, senão o operador marca quinze, vê doze marcados e procura um defeito. */
  marcadosSemNegocio = computed(() => [...this.selecionados()].filter(k => k < 0).length);

  todosMarcados = computed(() =>
    this.linhasDaPagina().length > 0
    && this.linhasDaPagina().every(k => this.selecionados().has(k)));

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
        // ===================== A PÁGINA QUE ESVAZIOU =====================
        // Reabrir tudo na página 3 de 3 tira as linhas da aba, e a recarga pede a página 3 de
        // novo. O servidor manda o total CERTO nessa página vazia (AUD-XX, B5) — antes mandava
        // 0, e a tela dizia "nenhum lead perdido" com cem nas páginas 1 e 2.
        //
        // A tela vai DIRETO para a última página que existe, pelo `totalPaginas` do servidor
        // (AUD-XX, #21). Antes voltava uma página de cada vez e pedia de novo.
        // =================================================================
        if (p.itens.length === 0 && p.totalCount > 0 && this.pagina() > p.totalPaginas) {
          this.pagina.set(p.totalPaginas);
          this.carregar();
          return;
        }

        this.itens.set(p.itens);
        this.total.set(p.totalCount);
        this.totalPaginas.set(p.totalPaginas);
        this.carregando.set(false);
        this.erro.set('');
      },
      error: e => {
        this.erro.set(e.error?.erro ?? 'Não foi possível carregar os leads parados. Tente de novo.');
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

  perdidos = computed(() => this.aba() === 'perdidos');

  trocarAba(aba: AbaDeLeads) {
    if (aba === this.aba()) return;

    this.aba.set(aba);
    // O resultado do lote anterior fala da outra lista: deixá-lo na tela faria o número parecer
    // desta.
    this.resultadoLote.set(null);
    this.doZero();
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

    // ⚠️ A MÉTRICA SEGUE O MESMO RECORTE, e DEPOIS da lista: sem isto, trocar o responsável
    // mudaria a lista e deixaria o número acima dela falando da equipe inteira — dois recortes
    // diferentes no mesmo olhar. Chamada direta, não `queueMicrotask`: o adiamento não servia a
    // nada e tornava a ordem das requisições imprevisível para quem lê (e para o teste).
    if (qual === 'responsavel' && this.metricaAberta()) this.carregarMetrica();
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

  /** ⚠️ REABRIR AGE SOBRE OS CONTATOS, não sobre as linhas: quem perdeu em dois funis aparece
   *  duas vezes e vira UMA reabertura — mesma redução do lembrete. */
  /** ===================== O RETORNO QUE NÃO SE PERDE =====================
   *  ⚠️ O AVISO ACIMA DA TABELA ERA O ÚNICO RETORNO, e é uma linha em cinza claro: quem estava
   *  rolando a lista não o via, e o relato de uso foi "criei o lembrete e não fez nada" — com os
   *  lembretes gravados no banco. O toast aparece onde quer que a pessoa esteja olhando. O aviso
   *  de cima continua: é ele que detalha quantos foram pulados e por quê.
   *
   *  Nada criado não é sucesso: o toast vira INFORMAÇÃO e manda ler o detalhe.
   *  ================================================================= */
  private avisar(criados: number, frase: string) {
    if (criados > 0) this.toast.sucesso(frase);
    else this.toast.info('Nada mudou: veja o motivo no aviso acima da lista.');
  }

  reabrirSelecionados() {
    if (this.contatosMarcados().length === 0 || this.salvandoLote()) return;

    this.salvandoLote.set(true);
    this.erro.set('');

    this.api.reabrir(this.contatosMarcados()).subscribe({
      next: r => {
        this.salvandoLote.set(false);
        this.carregar();
        this.ultimaAcao.set('reabrir');
        this.resultadoLote.set(r);
        this.avisar(r.criados, r.criados === 1 ? '1 negócio reaberto.' : `${r.criados} negócios reabertos.`);
      },
      error: e => {
        this.salvandoLote.set(false);
        this.erro.set(e.error?.erro ?? 'Não foi possível reabrir. Tente de novo.');
      }
    });
  }

  // ================================================================ a métrica
  // ⚠️ O APROVEITAMENTO NÃO É MAIS CALCULADO AQUI (AUD-XX). Era `ganhos / marcados` na tela; agora
  // chega pronto em `aproveitamentoPercentual`, e null — "—" — quando nada foi marcado.

  alternarMetrica() {
    const abrindo = !this.metricaAberta();

    this.metricaAberta.set(abrindo);

    if (!abrindo) return;

    // Os últimos 30 dias, que é a janela em que uma campanha de reativação ainda está viva.
    if (this.metricaDe() === '') {
      this.metricaDe.set(this.emDias(-30));
      this.metricaAte.set(this.emDias(0));
    }

    this.carregarMetrica();
  }

  trocarMetrica(qual: 'etiqueta' | 'de' | 'ate', valor: string) {
    if (qual === 'etiqueta') this.metricaEtiqueta.set(valor ? Number(valor) : null);
    else if (qual === 'de') this.metricaDe.set(valor);
    else this.metricaAte.set(valor);

    this.carregarMetrica();
  }

  carregarMetrica() {
    const etiquetaId = this.metricaEtiqueta();

    // Sem etiqueta não há pergunta: o bloco pede para escolher, em vez de mostrar zeros que
    // pareceriam resposta.
    if (etiquetaId === null || this.metricaDe() === '' || this.metricaAte() === '') {
      this.metrica.set(null);
      return;
    }

    this.carregandoMetrica.set(true);
    this.erroMetrica.set('');

    // ⚠️ MANDA O MESMO `responsavelId` DA LISTA. Com um recorte só na lista, a tela mostraria
    // "os leads parados da Ana" e, logo acima, o número da equipe inteira — dois recortes
    // diferentes no mesmo olhar. O servidor descarta o parâmetro de quem não pode usá-lo.
    this.api.reativacao(etiquetaId, this.metricaDe(), this.metricaAte(), this.responsavelId())
      .subscribe({
        next: r => {
          this.metrica.set(r);
          this.carregandoMetrica.set(false);
        },
        error: e => {
          this.erroMetrica.set(e.error?.erro ?? 'Não foi possível calcular. Tente de novo.');
          this.carregandoMetrica.set(false);
          this.metrica.set(null);
        }
      });
  }

  // ================================================================ a seleção e o lote

  marcado(l: LeadParado): boolean {
    return this.selecionados().has(this.chave(l));
  }

  alternar(l: LeadParado) {
    const novo = new Set(this.selecionados());
    const k = this.chave(l);

    if (!novo.delete(k)) novo.add(k);
    this.selecionados.set(novo);
  }

  desmarcarTudo() {
    this.selecionados.set(new Set());
  }

  /** Marca ou desmarca a página inteira. "Tudo" é a PÁGINA, nunca o resultado todo: o servidor
   *  aceita no máximo uma página por chamada, e um botão que marcasse 300 leads prometeria uma
   *  ação que volta 400. */
  alternarTodos() {
    this.selecionados.set(this.todosMarcados() ? new Set() : new Set(this.linhasDaPagina()));
  }

  abrirLote(qual: 'lembrete' | 'etiqueta' | 'responsavel') {
    if (this.quantosMarcados() === 0) return;

    this.erroLote.set('');
    this.resultadoLote.set(null);
    this.loteTitulo.set('Retomar contato');
    this.loteObs.set('');
    this.loteData.set(this.emDias(1));
    this.loteEtiqueta.set(null);
    this.loteResponsavel.set(undefined);
    this.loteAberto.set(qual);
  }

  fecharLote() {
    if (this.salvandoLote()) return;

    this.loteAberto.set(null);
  }

  /** Quantos dos marcados têm negócio aberto — o que a etiqueta cola e o que a redistribuição
   *  move. Chamava-se `alvosDaEtiqueta`, e o nome passou a mentir quando a segunda ação começou a
   *  usar o mesmo número: duas ações, um só alvo possível.
   *
   *  É menor que a seleção quando há lead sem negócio aberto, e a tela mostra os dois números
   *  antes de confirmar. */
  negociosMarcados = computed(() => this.negociacoesMarcadas().length);

  /** Os responsáveis que podem RECEBER. ⚠️ VEM DE `/relatorios/opcoes`, a mesma lista do filtro —
   *  e ela já é recortada pelo servidor: quem não pode ver a equipe recebe só a si mesmo, e o
   *  servidor recusa um alvo inativo ou de outra empresa de todo jeito. */
  alvos = computed(() => this.opcoes().responsaveis);

  confirmarResponsavel() {
    const escolhido = this.loteResponsavel();

    if (escolhido === undefined || this.negociosMarcados() === 0) return;

    this.salvandoLote.set(true);
    this.erroLote.set('');

    this.api.redistribuir(this.negociacoesMarcadas(), escolhido).subscribe({
      next: r => {
        this.salvandoLote.set(false);
        this.loteAberto.set(null);
        this.carregar();
        this.ultimaAcao.set('responsavel');
        this.resultadoLote.set(r);
        const para = escolhido === null
          ? 'ficou sem responsável'
          : `passou para ${this.opcoes().responsaveis.find(p => p.id === escolhido)?.nome ?? 'a pessoa escolhida'}`;
        this.avisar(r.criados, r.criados === 1
          ? `1 negócio ${para}.`
          : `${r.criados} negócios ${para.replace('ficou', 'ficaram').replace('passou', 'passaram')}.`);
      },
      error: e => {
        this.salvandoLote.set(false);
        this.erroLote.set(e.error?.erro ?? 'Não foi possível redistribuir. Tente de novo.');
      }
    });
  }

  confirmarEtiqueta() {
    const etiquetaId = this.loteEtiqueta();

    if (etiquetaId === null || this.negociosMarcados() === 0) return;

    this.salvandoLote.set(true);
    this.erroLote.set('');

    this.api.aplicarEtiqueta({
      negociacaoIds: this.negociacoesMarcadas(),
      etiquetaId
    }).subscribe({
      next: r => {
        this.salvandoLote.set(false);
        this.loteAberto.set(null);
        this.carregar();
        this.ultimaAcao.set('etiqueta');
        this.resultadoLote.set(r);
        const nome = this.etiquetas().find(e => e.id === etiquetaId)?.nome ?? 'escolhida';
        this.avisar(r.criados, `Etiqueta “${nome}” aplicada em ${r.criados} ${r.criados === 1 ? 'negócio' : 'negócios'}.`);
        // Marcar MUDA o número de marcados: deixar o bloco com o valor velho faria parecer que a
        // ação não teve efeito.
        if (this.metricaAberta()) this.carregarMetrica();
      },
      error: e => {
        this.salvandoLote.set(false);
        this.erroLote.set(e.error?.erro ?? 'Não foi possível aplicar a etiqueta. Tente de novo.');
      }
    });
  }

  confirmarLote() {
    const titulo = this.loteTitulo().trim();

    if (titulo === '' || this.loteData() === '' || this.quantosMarcados() === 0) return;

    this.salvandoLote.set(true);
    this.erroLote.set('');

    this.api.criarLembretes({
      // Os contatos DISTINTOS, não as linhas: a pessoa com dois negócios parados recebe uma
      // tarefa. O servidor também deduplica, e isto é o que faz o contador da tela não mentir.
      contatoIds: this.contatosMarcados(),
      dataAlvo: this.loteData(),
      titulo,
      observacao: this.loteObs().trim() || null
    }).subscribe({
      next: r => {
        this.salvandoLote.set(false);
        this.loteAberto.set(null);
        // Recarrega ANTES de guardar o resultado: `carregar()` apaga a seleção, e o aviso tem de
        // sobreviver a isso — é a única coisa na tela que diz quantos foram pulados.
        this.carregar();
        this.ultimaAcao.set('lembrete');
        this.resultadoLote.set(r);
        // ⚠️ A DATA E O LUGAR NA FRASE: o lembrete nasce para AMANHÃ por padrão e cai no Meu Dia de
        // quem cuida do lead — não de quem clicou. Sem dizer isso, "criei e não fez nada" foi
        // exatamente o relato de uso: ele não aparecia hoje em lugar nenhum.
        const dia = `${this.loteData().slice(8, 10)}/${this.loteData().slice(5, 7)}`;
        this.avisar(r.criados, r.criados === 1
          ? `1 lembrete criado para ${dia}. Aparece no Meu Dia de quem cuida do lead, nesse dia.`
          : `${r.criados} lembretes criados para ${dia}. Aparecem no Meu Dia de quem cuida de cada lead, nesse dia.`);
      },
      error: e => {
        this.salvandoLote.set(false);
        this.erroLote.set(e.error?.erro ?? 'Não foi possível criar os lembretes. Tente de novo.');
      }
    });
  }

  /** A data em `aaaa-mm-dd` pelo relógio LOCAL.
   *
   *  ⚠️ USA `chaveDia`, NÃO UMA SEGUNDA CÓPIA. Ela já existe para a mesma regra — `toISOString()`
   *  devolve a data em UTC e às 21h em Brasília já virou o dia —, e `semaforo.spec.ts` a guarda
   *  com um `Date` falso que morde até num runner em UTC. Uma cópia aqui ficaria sem essa guarda. */
  /** Negativo e zero também: a métrica pede "há 30 dias" e "hoje". */
  private emDias(n: number): string {
    const d = new Date();

    d.setDate(d.getDate() + n);

    return chaveDia(d);
  }

  /** "há 3 meses" responde a pergunta; "há 97 dias" é preciso e não responde.
   *
   *  ⚠️ OS DIAS E OS MESES VÊM DO SERVIDOR (AUD-XX). A tela dividia os dias por 30, e a Evolução
   *  usava meses de 30,44 dias; os meses agora são de calendário, contados lá. */
  tempoParado(dias: number, meses: number): string {
    if (dias < 1) return 'hoje';
    if (dias === 1) return 'ontem';
    if (meses < 1) return `há ${dias} dias`;

    return meses === 1 ? 'há 1 mês' : `há ${meses} meses`;
  }
}
