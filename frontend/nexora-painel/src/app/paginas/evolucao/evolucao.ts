import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { AuthServico } from '../../nucleo/servicos/auth.servico';
import { GraficoSparkline } from '../../nucleo/graficos/grafico-sparkline';
import {
  EvolucaoDaEquipe, EvolucaoDoVendedor, EvolucaoServico, JanelaEvolucao, MesDaConversao
} from '../../nucleo/servicos/evolucao.servico';

/** Um trecho de linha desenhado: `d` do `<path>` e se vai pontilhado. */
interface Traco {
  solido: string;
  tracejado: string;
}

/** ===================== EVOLUÇÃO (EVO-1) =====================
 *
 *  `/relatorios` responde "quanto, neste recorte". Esta tela responde "está melhorando?", e a
 *  diferença não é de apresentação: a conversão de lá muda o passado sozinha, porque o denominador
 *  dela conta por `contatos.responsavel_id` — coluna que a conclusão de uma venda ZERA. Aqui as
 *  duas pontas contam por `negociacoes.responsavel_id` e cada uma é datada pelo próprio
 *  fechamento.
 *
 *  ⚠️ TELA PRÓPRIA, E NÃO UM BLOCO EM RELATÓRIOS. Os oito blocos de lá leem um De/Até escolhido no
 *  topo; esta feature tem eixo de tempo PRÓPRIO (3, 6 ou 12 meses). Dentro de Relatórios ela
 *  ignoraria em silêncio o período da barra — e filtro que não governa o que está embaixo é pior
 *  que filtro nenhum.
 *
 *  ⚠️ O RECORTE POR PESSOA NÃO ESTÁ AQUI. Quem não tem `ver_numeros_da_equipe` recebe uma linha só
 *  e `equipe` NULO, decidido no servidor. A tela não esconde nada: ela não recebe. Se o recorte
 *  fosse daqui, bastaria abrir o painel do navegador para ver o time inteiro.
 *  ============================================================== */
@Component({
  selector: 'app-evolucao',
  imports: [RouterLink, GraficoSparkline],
  templateUrl: './evolucao.html',
  styleUrl: './evolucao.css'
})
export class Evolucao implements OnInit {
  private api = inject(EvolucaoServico);
  auth = inject(AuthServico);

  readonly janelas: JanelaEvolucao[] = [3, 6, 12];

  janela = signal<JanelaEvolucao>(6);
  dados = signal<EvolucaoDaEquipe | null>(null);
  carregando = signal(false);
  erro = signal('');

  /** Quem está aberto no bloco de detalhe, por id. `'sem-dono'` porque a linha sem responsável não
   *  tem id e `null` já significa "ninguém selecionado". */
  selecionadoId = signal<number | 'sem-dono' | null>(null);

  // ---------------------------------------------------------------- o desenho
  readonly W = 1000;
  readonly H = 300;
  readonly pad = 14;

  ngOnInit() {
    this.carregar();
  }

  trocarJanela(meses: JanelaEvolucao) {
    if (meses === this.janela()) return;

    this.janela.set(meses);
    // A seleção SOBREVIVE à troca de janela: quem está olhando uma pessoa em 6 meses e pede 12
    // quer a MESMA pessoa com mais história, não voltar para a tabela.
    this.carregar();
  }

  carregar() {
    this.carregando.set(true);
    this.erro.set('');

    this.api.obter(this.janela()).subscribe({
      next: d => {
        this.dados.set(d);
        this.carregando.set(false);
      },
      error: () => {
        this.erro.set('Não foi possível carregar a evolução. Tente de novo.');
        this.carregando.set(false);
      }
    });
  }

  selecionar(p: EvolucaoDoVendedor) {
    const chave = p.usuarioId ?? 'sem-dono';

    this.selecionadoId.set(this.selecionadoId() === chave ? null : chave);
  }

  /** As linhas da tabela: a régua PRIMEIRO, quando existe.
   *
   *  ⚠️ A EQUIPE É A PRIMEIRA LINHA, não a última. 35% não diz se é bom sem ela — depende do time.
   *  Pôr a régua no rodapé obrigaria a rolar até o fim e voltar para cada pessoa. */
  linhas = computed<EvolucaoDoVendedor[]>(() => {
    const d = this.dados();
    if (!d) return [];

    return d.equipe ? [d.equipe, ...d.pessoas] : d.pessoas;
  });

  /** ⚠️ UMA ESCALA PARA TODAS AS MINIATURAS. Escala por linha faria duas pessoas com histórias
   *  completamente diferentes desenharem o mesmo morro, lado a lado numa tabela que convida a
   *  comparar. Calculada aqui porque só a tela vê o conjunto. */
  escala = computed(() => {
    const valores = this.linhas()
      .flatMap(p => p.meses.map(m => m.conversaoPercentual))
      .filter((v): v is number => v !== null);

    if (valores.length === 0) return { minimo: 0, maximo: 100 };

    const minimo = Math.min(...valores);
    const maximo = Math.max(...valores);

    // Faixa mínima de 10 pontos: sem ela, uma equipe estável entre 30% e 31% viraria uma serra.
    if (maximo - minimo >= 10) return { minimo, maximo };

    const meio = (minimo + maximo) / 2;

    return { minimo: Math.max(0, meio - 5), maximo: Math.min(100, meio + 5) };
  });

  serieDe(p: EvolucaoDoVendedor): (number | null)[] {
    return p.meses.map(m => m.conversaoPercentual);
  }

  /** ===================== O GRAFICO MUDO, E POR QUE ELE NAO PODE SUMIR =====================
   *
   *  Uma linha precisa de DOIS pontos. Com negocio decidido num mes so — empresa nova, ou todo o
   *  movimento no mes corrente — nao ha o que desenhar, e desenhar um ponto solitario seria pior:
   *  a escala o poria no alto, insinuando um pico que nao se mediu contra nada.
   *
   *  ⚠️ MAS SUMIR TAMBEM E ERRADO, E ESTE PROJETO JA PAGOU POR ISSO. O commit `8e580e0` existe
   *  porque o `grafico-linha` virava um retangulo vazio com um periodo so: nem desenho, nem
   *  explicacao. A primeira versao desta tela repetiu o erro de outro jeito — escondia o cartao
   *  inteiro, e a tabela embaixo mostrava numeros sem nada dizer por que o grafico nao veio.
   *
   *  Entao o cartao fica, com a razao escrita. E a razao e POR CASO: "o mes ainda esta em
   *  andamento" e "so ha um mes com movimento" levam a acoes diferentes — esperar o mes fechar, ou
   *  esperar o proximo mes.
   *  ====================================================================================== */
  recadoSemLinha = computed(() => {
    const meses = this.mesesDaJanela();
    const comMovimento = meses.filter(m => m.decididos > 0);

    if (comMovimento.length === 0) return '';

    const fechadosComMovimento = comMovimento.filter(m => !m.parcial);

    if (fechadosComMovimento.length === 0) {
      const corrente = meses[meses.length - 1];

      return `Tudo o que foi decidido está em ${this.mesLongo(corrente)}, que ainda não fechou. `
        + 'A linha aparece quando houver dois meses fechados para comparar.';
    }

    return 'Um mês só com negócio decidido. Uma linha precisa de dois pontos para dizer se subiu '
      + 'ou caiu — ela aparece no próximo mês.';
  });

  // ---------------------------------------------------------------- o detalhe

  selecionado = computed<EvolucaoDoVendedor | null>(() => {
    const chave = this.selecionadoId();
    if (chave === null) return null;

    const alvo = chave === 'sem-dono' ? null : chave;

    return this.linhas().find(p => p.usuarioId === alvo) ?? null;
  });

  /** O topo do eixo do gráfico grande, arredondado para a dezena de ponto acima.
   *
   *  ⚠️ COMEÇA EM ZERO, diferente das miniaturas. O gráfico grande tem espaço para linhas de
   *  referência e rótulos, e base cortada num gráfico com eixo visível é o jeito clássico de
   *  exagerar variação pequena. Na miniatura não há eixo nem rótulo, e o que ela responde é só
   *  "para onde foi". */
  topo = computed(() => {
    const valores = this.linhas()
      .flatMap(p => p.meses.map(m => m.conversaoPercentual))
      .filter((v): v is number => v !== null);

    const maior = valores.length > 0 ? Math.max(...valores) : 10;

    // Em pontos de 0 a 100 (AUD-XX): o topo sobe de 10 em 10.
    return Math.max(10, Math.ceil(maior / 10) * 10);
  });

  /** Os meses da janela. Vem da equipe ou da primeira pessoa — todas as linhas têm os MESMOS
   *  meses, porque o serviço preenche com zero quem não fechou nada. É o que permite um eixo só. */
  mesesDaJanela = computed<MesDaConversao[]>(() =>
    (this.dados()?.equipe ?? this.dados()?.pessoas[0])?.meses ?? []);

  /** ⚠️ O EIXO É DA JANELA, NÃO DE QUEM ESTÁ SELECIONADO. Enquanto o gráfico era só do detalhe
   *  dava no mesmo; com todas as pessoas no mesmo desenho, derivar o eixo de uma delas faria as
   *  outras saírem deslocadas no dia em que uma tivesse menos meses. */
  x(i: number): number {
    const n = this.mesesDaJanela().length;

    return n <= 1 ? this.W / 2 : this.pad + (i / (n - 1)) * (this.W - 2 * this.pad);
  }

  y(v: number): number {
    return this.H - this.pad - (v / this.topo()) * (this.H - 2 * this.pad);
  }

  /** ⚠️ O MÊS EM ANDAMENTO SAI PONTILHADO, e é a única diferença visual que separa "caiu" de
   *  "ainda não acabou". Desenhá-lo sólido faria todo dia 2 do mês parecer um desabamento. */
  private tracoDe(meses: MesDaConversao[]): Traco {
    const fechados: string[] = [];
    let anterior = -2;

    const valido = (m: MesDaConversao, i: number) => m.conversaoPercentual !== null && !m.parcial
      ? { v: m.conversaoPercentual, i }
      : null;

    const pontos = meses.map(valido).filter((p): p is { v: number; i: number } => p !== null);

    const trechos: string[] = [];
    let atual: string[] = [];

    for (const p of pontos) {
      if (p.i !== anterior + 1 && atual.length > 0) {
        if (atual.length > 1) trechos.push(atual.join(' '));
        atual = [];
      }
      atual.push(`${atual.length ? 'L' : 'M'}${this.x(p.i)},${this.y(p.v)}`);
      anterior = p.i;
    }
    if (atual.length > 1) trechos.push(atual.join(' '));
    fechados.push(...trechos);

    // A perna pontilhada: do último mês fechado até o mês em andamento.
    const ultimo = meses[meses.length - 1];
    const penultimo = pontos.at(-1);
    const tracejado = ultimo?.parcial && ultimo.conversaoPercentual !== null && penultimo
      ? `M${this.x(penultimo.i)},${this.y(penultimo.v)} ` +
        `L${this.x(meses.length - 1)},${this.y(ultimo.conversaoPercentual)}`
      : '';

    return { solido: fechados.join(' '), tracejado };
  }

  /** ===================== A COR É INFORMAÇÃO, NÃO ETIQUETA =====================
   *
   *  A paleta do produto é verde, creme e UM tom de alerta — `dashboard.spec.ts` tem teste que
   *  falha se um gráfico sair dela. Então "uma cor por pessoa" simplesmente não está disponível, e
   *  inventar quatro verdes quase iguais entregaria uma distinção que o olho não faz.
   *
   *  ⚠️ A LINHA É PINTADA PELA TENDÊNCIA, igual ao selo e à miniatura da mesma fileira. Quem é
   *  quem vem da LEGENDA, que é texto e não depende de o olho separar dois verdes. O que a cor
   *  responde é "quem está subindo e quem está caindo" — e é essa a pergunta da tela.
   *
   *  Duas pessoas melhorando saem da mesma cor, e está certo: a leitura "o time inteiro está
   *  subindo" é verdadeira e aparece de graça.
   *  ============================================================================ */
  corDaTendencia(t: string): string {
    if (t === 'melhorando') return 'var(--verde-3)';
    if (t === 'piorando') return 'var(--alerta)';

    return 'var(--texto-fraco)';
  }

  /** Uma linha por pessoa, para o gráfico de abertura.
   *
   *  ⚠️ QUEM NÃO FECHOU NADA NÃO VIRA LINHA. `tracoDe` devolve vazio, e desenhar nada é o certo —
   *  uma reta no zero diria que a pessoa tentou e não fechou, que é o oposto de não ter tentado. A
   *  linha dela continua na tabela, com travessão. */
  series = computed(() =>
    (this.dados()?.pessoas ?? [])
      .map(p => ({
        pessoa: p,
        traco: this.tracoDe(p.meses),
        cor: this.corDaTendencia(p.tendencia)
      }))
      .filter(l => l.traco.solido !== '' || l.traco.tracejado !== ''));

  tracoPessoa = computed(() => this.tracoDe(this.selecionado()?.meses ?? []));
  tracoEquipe = computed(() => this.tracoDe(this.dados()?.equipe?.meses ?? []));

  bolinhas = computed(() =>
    (this.selecionado()?.meses ?? [])
      .map((m, i) => ({ m, i }))
      .filter(p => p.m.conversaoPercentual !== null)
      .map(p => ({
        cx: this.x(p.i),
        cy: this.y(p.m.conversaoPercentual as number),
        parcial: p.m.parcial,
        fraca: p.m.amostraInsuficiente
      })));

  /** As faixas esmaecidas atrás dos meses que não votam. */
  faixasFracas = computed(() => {
    const meses = this.selecionado()?.meses ?? [];
    if (meses.length < 2) return [];

    const meia = (this.W - 2 * this.pad) / (meses.length - 1) / 2;

    return meses
      .map((m, i) => ({ m, i }))
      .filter(p => p.m.amostraInsuficiente)
      .map(p => ({
        x: Math.max(0, this.x(p.i) - meia),
        largura: meia * 2
      }));
  });

  // ---------------------------------------------------------------- texto

  readonly MESES_CURTOS = [
    'jan', 'fev', 'mar', 'abr', 'mai', 'jun', 'jul', 'ago', 'set', 'out', 'nov', 'dez'
  ];

  readonly MESES_LONGOS = [
    'janeiro', 'fevereiro', 'março', 'abril', 'maio', 'junho',
    'julho', 'agosto', 'setembro', 'outubro', 'novembro', 'dezembro'
  ];

  mesCurto(m: MesDaConversao): string {
    return this.MESES_CURTOS[m.mes - 1] ?? '';
  }

  mesLongo(m: MesDaConversao): string {
    return this.MESES_LONGOS[m.mes - 1] ?? '';
  }

  /** ⚠️ NULO VIRA TRAVESSÃO, NUNCA "0%". A tela inteira depende disto: "0%" afirma que a pessoa
   *  tentou e não fechou, e o travessão diz que não houve o que medir. */
  pct(v: number | null): string {
    // Só formata: o percentual chega pronto, de 0 a 100 (AUD-XX). A tela mostra inteiro.
    return v === null ? '—' : `${v.toLocaleString('pt-BR', { maximumFractionDigits: 0 })}%`;
  }

  /** Em PONTOS percentuais, com o sinal explícito. De 25% para 30% são +5 p.p.; chamar isso de
   *  "+20%" está certo na conta e lê errado na tela. */
  pontosDe(v: number | null): string {
    if (v === null) return '';

    const sinal = v > 0 ? '+' : v < 0 ? '−' : '';

    return `${sinal}${Math.abs(v).toFixed(1).replace('.', ',')} p.p.`;
  }

  rotuloTendencia(p: EvolucaoDoVendedor): string {
    if (p.tendencia === 'sem_dados') return 'dados insuficientes';

    const nome = p.tendencia === 'estavel' ? 'estável' : p.tendencia;

    return p.variacaoPontos === null || p.tendencia === 'estavel'
      ? nome
      : `${nome} ${this.pontosDe(p.variacaoPontos)}`;
  }

  /** Tempo de casa em texto aproximado. Meses e anos bastam: "há 412 dias" é preciso e não
   *  responde a pergunta, que é "esta pessoa é nova?".
   *
   *  ⚠️ OS MESES VÊM DO SERVIDOR (AUD-XX), em meses de calendário completos. A tela os calculava
   *  com meses de 30,44 dias — e Leads parados com 30. Aqui só se escolhe a unidade. */
  tempoDeCasa(meses: number | null): string {
    if (meses === null) return '—';

    if (meses < 1) return 'este mês';
    if (meses < 12) return `há ${meses} ${meses === 1 ? 'mês' : 'meses'}`;

    const anos = Math.floor(meses / 12);

    return `há ${anos} ${anos === 1 ? 'ano' : 'anos'}`;
  }

  /** O recorte escrito no topo: "mar a ago de 2026 · agosto em andamento". */
  periodo = computed(() => {
    const d = this.dados();
    if (!d) return '';

    const meses = (d.equipe ?? d.pessoas[0])?.meses ?? [];
    if (meses.length === 0) return '';

    const primeiro = meses[0];
    const ultimo = meses[meses.length - 1];
    const ano = primeiro.ano === ultimo.ano
      ? `de ${ultimo.ano}`
      : `de ${primeiro.ano} a ${ultimo.ano}`;

    const andamento = ultimo.parcial ? ` · ${this.mesLongo(ultimo)} em andamento` : '';

    return `${this.mesCurto(primeiro)} a ${this.mesCurto(ultimo)} ${ano}${andamento}`;
  });

  /** O título diz a verdade sobre o que está na tela: quem recebe uma linha só não está vendo a
   *  equipe. A fonte é o `equipe` que o SERVIDOR mandou — não a lista de permissões local. */
  titulo = computed(() => this.dados()?.equipe ? 'Evolução da equipe' : 'Minha evolução');

  ehEquipe(p: EvolucaoDoVendedor): boolean {
    return p === this.dados()?.equipe;
  }

  chaveDe(p: EvolucaoDoVendedor): string {
    return p.usuarioId === null ? `sem-dono-${p.nome}` : String(p.usuarioId);
  }
}
