import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { AuthServico } from '../../nucleo/servicos/auth.servico';
import { PERMISSOES_DE } from '../../nucleo/seguranca/permissoes-de-teste';
import {
  EvolucaoDaEquipe, EvolucaoDoVendedor, MesDaConversao
} from '../../nucleo/servicos/evolucao.servico';
import { Evolucao } from './evolucao';

/** ===================== EVOLUÇÃO (EVO-1) =====================
 *
 *  ⚠️ O QUE ESTA SUÍTE GUARDA É A HONESTIDADE DA LEITURA, não o desenho. Três coisas, e as três
 *  são jeitos de a tela mentir sem errar nenhuma conta:
 *
 *    · nulo pintado como "0%" — férias viram fracasso;
 *    · mês de amostra pequena mostrado igual a um mês cheio — 2 de 3 = 67% engana quem lê rápido;
 *    · a régua da equipe aparecendo para quem a permissão fecha.
 *  ============================================================== */
describe('evolução (EVO-1)', () => {
  let fixture: ComponentFixture<Evolucao>;
  let c: Evolucao;
  let http: HttpTestingController;

  function mes(
    m: number, decididos: number, ganhos: number,
    extra: Partial<MesDaConversao> = {}): MesDaConversao {
    return {
      ano: 2026, mes: m, decididos, ganhos,
      conversaoPercentual: decididos === 0 ? null : Math.round(ganhos / decididos * 10000) / 100,
      parcial: false,
      amostraInsuficiente: decididos > 0 && decididos < 10,
      ...extra
    };
  }

  function pessoa(
    nome: string, usuarioId: number | null,
    meses: MesDaConversao[], extra: Partial<EvolucaoDoVendedor> = {}): EvolucaoDoVendedor {
    const decididos = meses.reduce((a, m) => a + m.decididos, 0);
    const ganhos = meses.reduce((a, m) => a + m.ganhos, 0);

    return {
      usuarioId, nome, noNexoraDesde: '2024-01-10T00:00:00Z', mesesNoNexora: 30,
      decididos, ganhos,
      conversaoPercentual: decididos === 0 ? null : Math.round(ganhos / decididos * 10000) / 100,
      variacaoPontos: null, tendencia: 'sem_dados', meses,
      ...extra
    };
  }

  const SEIS = [
    mes(3, 20, 4), mes(4, 20, 4), mes(5, 20, 5),
    mes(6, 20, 5), mes(7, 20, 6), mes(8, 12, 4, { parcial: true })
  ];

  const ANA = pessoa('Ana Souza', 7, SEIS, { variacaoPontos: 5.0, tendencia: 'melhorando' });
  const BRUNO = pessoa('Bruno Lima', 8,
    [mes(3, 20, 9), mes(4, 20, 8), mes(5, 20, 6), mes(6, 20, 5), mes(7, 20, 4),
     mes(8, 10, 2, { parcial: true })],
    { variacaoPontos: -14.2, tendencia: 'piorando' });

  const RESPOSTA: EvolucaoDaEquipe = {
    equipe: pessoa('Toda a equipe', null, SEIS, { variacaoPontos: 0.8, tendencia: 'estavel' }),
    pessoas: [ANA, BRUNO],
    de: '2026-03-01',
    ate: '2026-08-31'
  };

  function montar(papel: 'dono' | 'vendedor' = 'dono', corpo: EvolucaoDaEquipe = RESPOSTA) {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting()
      ]
    });

    TestBed.inject(AuthServico).aplicarLogin({
      token: 'tok',
      usuario: {
        id: 1, nome: 'Ana', email: 'a@x.com', papel,
        permissoes: PERMISSOES_DE[papel], empresaNome: 'Padaria'
      }
    } as never);

    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(Evolucao);
    c = fixture.componentInstance;
    fixture.detectChanges();

    for (const r of http.match(() => true)) r.flush(corpo);
    fixture.detectChanges();
  }

  function raiz(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  afterEach(() => {
    http.verify();
    TestBed.resetTestingModule();
  });

  // ==================================================================== a régua

  it('A EQUIPE É A PRIMEIRA LINHA, e não a última', () => {
    montar();

    const nomes = [...raiz().querySelectorAll('.tabela-evolucao tbody tr')]
      .map(tr => tr.querySelector('td')!.textContent!.trim());

    // 35% não diz se é bom sem a régua, e régua no rodapé obriga a rolar até o fim e voltar
    // para cada pessoa.
    expect(nomes).toEqual(['Toda a equipe', 'Ana Souza', 'Bruno Lima']);
    expect(raiz().querySelector('.tabela-evolucao tbody tr')!.classList).toContain('regua');
  });

  it('O TÍTULO DIZ A VERDADE SOBRE O QUE ESTÁ NA TELA', () => {
    montar();
    expect(raiz().querySelector('h1')!.textContent!.trim()).toBe('Evolução da equipe');
  });

  /** ⚠️ O PAR DO TESTE DE CIMA. Quem recebe uma linha só NÃO está vendo a equipe, e manter o
   *  título "Evolução da equipe" para essa pessoa seria a tela afirmando o que ela não mostra. */
  it('QUEM NÃO VÊ A EQUIPE LÊ "MINHA EVOLUÇÃO", e não vê régua nenhuma', () => {
    montar('vendedor', { ...RESPOSTA, equipe: null, pessoas: [ANA] });

    expect(raiz().querySelector('h1')!.textContent!.trim()).toBe('Minha evolução');
    expect(raiz().querySelector('.tabela-evolucao tr.regua')).toBeNull();

    const nomes = [...raiz().querySelectorAll('.tabela-evolucao tbody tr')]
      .map(tr => tr.querySelector('td')!.textContent!.trim());
    expect(nomes).toEqual(['Ana Souza']);
  });

  // ==================================================================== nulo não é zero

  /** ===================== O TESTE QUE A TELA EXISTE PARA PASSAR =====================
   *  ⚠️ `conversao: null` TEM DE SAIR COMO TRAVESSÃO. Um `?? 0` em qualquer ponto do caminho faria
   *  um mês sem nada decidido aparecer como "0%" — e "0%" afirma que a pessoa tentou e não fechou.
   *  É o que o relatório antigo faz, devolvendo `0` nos dois casos, e é metade da razão de esta
   *  tela existir.
   *  ============================================================== */
  it('MÊS SEM NADA DECIDIDO SAI COMO TRAVESSÃO, nunca como 0%', () => {
    const vazio = pessoa('Carla Dias', 9, [
      mes(3, 0, 0), mes(4, 20, 5), mes(5, 20, 5),
      mes(6, 20, 5), mes(7, 20, 5), mes(8, 5, 1, { parcial: true })
    ]);

    montar('dono', { ...RESPOSTA, pessoas: [vazio] });

    expect(c.pct(null)).toBe('—');
    expect(c.pct(0)).toBe('0%');

    c.selecionar(vazio);
    fixture.detectChanges();

    const marco = raiz().querySelector('.tabela-meses tbody tr')!;
    expect(marco.textContent).toContain('março');
    expect([...marco.querySelectorAll('td')].map(t => t.textContent!.trim()))
      .toEqual(['março', '0', '0', '—', '']);
  });

  // ==================================================================== o volume

  /** ⚠️ O DENOMINADOR FICA À VISTA, e não num tooltip. "2 de 3 = 67%" engana quem lê rápido, e
   *  esconder o volume é o que torna isso possível. A marca de amostra insuficiente sozinha não
   *  basta: ela diz que há um problema, o volume diz qual. */
  it('O MÊS DE AMOSTRA PEQUENA APARECE, com o volume ao lado e a razão escrita', () => {
    const poucos = pessoa('Dê Nunes', 10, [
      mes(3, 3, 2), mes(4, 20, 5), mes(5, 20, 5),
      mes(6, 20, 5), mes(7, 20, 5), mes(8, 20, 5, { parcial: true })
    ]);

    montar('dono', { ...RESPOSTA, pessoas: [poucos] });

    c.selecionar(poucos);
    fixture.detectChanges();

    const marco = raiz().querySelector('.tabela-meses tbody tr')!;

    expect(marco.classList).withContext('o mês que não vota fica esmaecido').toContain('fora');
    expect([...marco.querySelectorAll('td')].map(t => t.textContent!.trim()).slice(0, 4))
      .toEqual(['março', '3', '2', '67%']);
    expect(marco.textContent).toContain('amostra insuficiente');
    expect(marco.textContent).toContain('fora da tendência');
  });

  it('O MÊS EM ANDAMENTO DIZ QUE ESTÁ EM ANDAMENTO, e não "caiu"', () => {
    montar();

    c.selecionar(ANA);
    fixture.detectChanges();

    const linhas = [...raiz().querySelectorAll('.tabela-meses tbody tr')];
    const agosto = linhas[linhas.length - 1];

    expect(agosto.textContent).toContain('agosto');
    expect(agosto.textContent).toContain('em andamento');
    expect(agosto.classList).toContain('fora');

    // E a perna final do gráfico sai pontilhada: é a única diferença visual entre "caiu" e
    // "ainda não acabou".
    expect(c.tracoPessoa().tracejado).withContext('o mês parcial tem perna própria').not.toBe('');
  });

  // ==================================================================== a tendência

  it('A TENDÊNCIA VEM EM PONTOS PERCENTUAIS, com o sinal e a vírgula do pt-BR', () => {
    montar();

    // De 25% para 30% são +5 p.p.; "+20%" está certo na conta e lê errado na tela.
    expect(c.rotuloTendencia(ANA)).toBe('melhorando +5,0 p.p.');
    expect(c.rotuloTendencia(BRUNO)).toBe('piorando −14,2 p.p.');
  });

  it('ESTÁVEL E SEM DADOS NÃO MOSTRAM NÚMERO, porque não afirmam movimento', () => {
    montar();

    expect(c.rotuloTendencia(
      pessoa('X', 1, SEIS, { tendencia: 'estavel', variacaoPontos: 0.4 }))).toBe('estável');
    expect(c.rotuloTendencia(
      pessoa('Y', 2, SEIS, { tendencia: 'sem_dados' }))).toBe('dados insuficientes');
  });

  it('O SELO LEVA A CLASSE DA TENDÊNCIA, que é o que separa verde de vermelho', () => {
    montar();

    const selos = [...raiz().querySelectorAll('.tabela-evolucao tbody tr .tendencia')]
      .map(e => e.className);

    expect(selos[0]).withContext('a régua não tem selo de tendência').toContain('melhorando');
    expect(selos[1]).toContain('piorando');
  });

  // ==================================================================== o gráfico de abertura

  /** ===================== A TELA ABRE COM A FORMA, NÃO SÓ COM OS NÚMEROS =====================
   *  ⚠️ ESTE BLOCO NASCEU DE UM DEFEITO DE PRODUTO. A primeira versão era só tabela, com o
   *  gráfico atrás de um clique — e "quem está subindo e quem está caindo" é pergunta que se
   *  responde por FORMA. Forma pedida por clique é forma que ninguém vê.
   *  ============================================================================================ */
  it('O GRÁFICO DE ABERTURA DESENHA UMA LINHA POR PESSOA', () => {
    montar();

    const g = raiz().querySelector('.grafico-equipe')!;
    expect(g).withContext('o cartão do gráfico abre junto com a tela').not.toBeNull();

    expect(c.series().map(l => l.pessoa.nome)).toEqual(['Ana Souza', 'Bruno Lima']);
    expect(g.querySelectorAll('svg.grafico g').length).toBe(2);
  });

  /** ⚠️ A COR É INFORMAÇÃO. A paleta é verde + creme + um alerta, com teste que falha se um
   *  gráfico sair dela — então "uma cor por pessoa" não existe. A linha é pintada pela TENDÊNCIA,
   *  igual ao selo e à miniatura da mesma fileira, e quem é quem vem da legenda. */
  it('A COR DA LINHA SEGUE A TENDÊNCIA, e não a pessoa', () => {
    montar();

    expect(c.series().map(l => l.cor)).toEqual(['var(--verde-3)', 'var(--alerta)']);
    expect(c.corDaTendencia('estavel')).toBe('var(--texto-fraco)');
    expect(c.corDaTendencia('sem_dados')).toBe('var(--texto-fraco)');
  });

  /** Quem não fechou nada não vira linha: uma reta no zero diria "tentou e não fechou", que é o
   *  oposto de não ter tentado. A pessoa continua na tabela, com travessão. */
  it('QUEM NÃO FECHOU NADA NÃO VIRA LINHA, mas continua na tabela', () => {
    const parado = pessoa('Zé Parado', 12, [
      mes(3, 0, 0), mes(4, 0, 0), mes(5, 0, 0),
      mes(6, 0, 0), mes(7, 0, 0), mes(8, 0, 0, { parcial: true })
    ]);

    montar('dono', { ...RESPOSTA, pessoas: [ANA, parado] });

    expect(c.series().map(l => l.pessoa.nome)).toEqual(['Ana Souza']);

    const nomes = [...raiz().querySelectorAll('.tabela-evolucao tbody tr')]
      .map(tr => tr.querySelector('td')!.textContent!.trim());
    expect(nomes).toContain('Zé Parado');
  });

  it('A RÉGUA DA EQUIPE ENTRA TRACEJADA, como referência e não como protagonista', () => {
    montar();

    const regua = raiz().querySelector('.grafico-equipe svg.grafico > path[stroke-dasharray="5 4"]');
    expect(regua).not.toBeNull();
    expect(regua!.getAttribute('stroke')).toBe('var(--texto-fraco)');
  });

  it('CLICAR NA LEGENDA SELECIONA, igual a clicar na fileira', () => {
    montar();

    const chaves = [...raiz().querySelectorAll('.grafico-equipe .chave-botao')] as HTMLButtonElement[];
    expect(chaves.map(b => b.textContent!.trim())).toEqual(['Ana Souza', 'Bruno Lima']);

    chaves[1].click();
    fixture.detectChanges();

    expect(c.selecionado()!.nome).toBe('Bruno Lima');
    expect(chaves[1].getAttribute('aria-pressed')).toBe('true');
    // A outra recua, no gráfico e na legenda, pelo mesmo motivo.
    expect(chaves[0].classList).toContain('apagada');
    expect(chaves[1].classList).not.toContain('apagada');
  });

  /** ⚠️ O EIXO É DA JANELA, NÃO DE QUEM ESTÁ SELECIONADO. Enquanto o gráfico era só do detalhe
   *  dava no mesmo; com todas as pessoas no mesmo desenho, derivar o eixo de uma delas faria as
   *  outras saírem deslocadas no dia em que alguém tivesse menos meses. */
  it('O EIXO E A ESCALA VALEM PARA TODO MUNDO, não para quem está aberto', () => {
    montar();

    const meses = [...raiz().querySelectorAll('.grafico-equipe .eixo > span')]
      .map(e => e.textContent!.trim());
    expect(meses).toEqual(['mar', 'abr', 'mai', 'jun', 'jul', 'ago']);

    // ⚠️ O VALOR ABSOLUTO, E NÃO "ANTES É IGUAL A DEPOIS". A primeira versão deste teste comparava
    // o topo antes e depois de abrir alguém — e a sabotagem de derivar a escala do selecionado
    // PASSAVA, porque ela mudava os dois lados igualmente. O que se afirma é que o eixo cobre a
    // linha mais alta de QUALQUER pessoa: o pico da janela é o 45% do Bruno, então o topo é 50%.
    // Em pontos de 0 a 100 desde o AUD-XX.
    expect(c.topo()).withContext('o eixo tem de caber o pico do Bruno').toBe(50);

    // E ANA, não Bruno, de propósito: o pico dela é 30%, então uma escala derivada de quem está
    // aberto encolheria para 40% e deixaria a linha do Bruno fora do desenho.
    c.selecionar(ANA);
    fixture.detectChanges();

    expect(c.topo()).withContext('abrir alguém não pode encolher o eixo').toBe(50);
    expect(c.x(0)).toBe(c.pad);
  });

  it('SEM NINGUÉM COM DADOS, O CARTÃO DO GRÁFICO NÃO APARECE', () => {
    montar('dono', { equipe: null, pessoas: [], de: '2026-03-01', ate: '2026-08-31' });

    expect(raiz().querySelector('.grafico-equipe')).toBeNull();
  });

  /** ===================== O TRAÇO NÃO PODE ESTICAR JUNTO COM O DESENHO =====================
   *
   *  Os dois gráficos usam `preserveAspectRatio="none"` — é o que faz a linha ocupar a largura
   *  toda em qualquer tela. O efeito colateral é que o SVG escala X e Y de forma diferente, e o
   *  TRAÇO escala junto: fica fino na vertical, grosso na horizontal, e o tracejado sai com
   *  espaçamento irregular.
   *
   *  ⚠️ ACHADO COMPARANDO COM O `grafico-linha`, que põe `vector-effect="non-scaling-stroke"` em
   *  todo path. A primeira versão desta tela não punha em nenhum — o defeito é visual puro e
   *  nenhum teste de comportamento o pegaria.
   *
   *  A asserção é sobre TODOS os desenhos, e não sobre os que existem hoje: é o que faz o próximo
   *  `<path>` nascer certo em vez de nascer distorcido em silêncio.
   *  ======================================================================================== */
  it('TODO TRAÇO DOS GRÁFICOS TEM NON-SCALING-STROKE', () => {
    montar();

    c.selecionar(ANA);
    fixture.detectChanges();

    const svgs = [...raiz().querySelectorAll('svg.grafico')];
    expect(svgs.length).withContext('o de abertura e o do detalhe').toBe(2);

    const semAtributo: string[] = [];
    for (const svg of svgs) {
      for (const d of svg.querySelectorAll('path, circle')) {
        if (d.getAttribute('vector-effect') !== 'non-scaling-stroke') {
          semAtributo.push(d.tagName + ' ' + (d.getAttribute('stroke') ?? d.getAttribute('fill')));
        }
      }
    }

    expect(semAtributo).withContext('traço que vai esticar junto com o desenho').toEqual([]);
  });

  // ==================================================================== o grafico mudo

  /** ===================== SUMIR NAO E UMA RESPOSTA =====================
   *
   *  ⚠️ ESTE PAR NASCEU DE UM DEFEITO EM PRODUCAO, e de um que este projeto JA TINHA CONSERTADO:
   *  o commit `8e580e0` existe porque o `grafico-linha` virava um retangulo vazio com um periodo
   *  so — nem desenho, nem explicacao. A primeira versao desta tela repetiu o erro de outro jeito,
   *  escondendo o cartao inteiro: a tabela embaixo mostrava numeros e nada dizia por que o grafico
   *  nao veio.
   *
   *  ⚠️ E A RAZAO E POR CASO. "o mes ainda esta em andamento" e "so ha um mes com movimento" levam
   *  a acoes diferentes: esperar o mes fechar, ou esperar o proximo. Um texto unico para os dois
   *  passaria neste teste e nao ajudaria ninguem.
   *  ============================================================================ */
  it('COM TUDO NO MES EM ANDAMENTO, O CARTAO FICA E DIZ QUE O MES NAO FECHOU', () => {
    const vazios = [mes(3, 0, 0), mes(4, 0, 0), mes(5, 0, 0), mes(6, 0, 0), mes(7, 0, 0)];
    const soAgora = pessoa('Sidinaldo', 9, [...vazios, mes(8, 10, 10, { parcial: true })]);

    montar('dono', {
      ...RESPOSTA,
      equipe: pessoa('Toda a equipe', null, [...vazios, mes(8, 10, 10, { parcial: true })]),
      pessoas: [soAgora]
    });

    expect(c.series().length).withContext('um ponto so nao vira linha').toBe(0);

    const cartao = raiz().querySelector('.grafico-equipe');
    expect(cartao).withContext('o cartao NAO pode sumir').not.toBeNull();
    expect(cartao!.querySelector('svg.grafico')).withContext('e nao desenha nada').toBeNull();

    const recado = cartao!.querySelector('.sem-linha-ainda')!.textContent!;
    expect(recado).toContain('agosto');
    expect(recado).toContain('ainda não fechou');
  });

  it('COM UM MES FECHADO SO, O RECADO E OUTRO', () => {
    const vazios = [mes(3, 0, 0), mes(4, 0, 0), mes(5, 0, 0), mes(6, 0, 0)];
    const umMes = pessoa('Sidinaldo', 9, [...vazios, mes(7, 20, 9), mes(8, 0, 0, { parcial: true })]);

    montar('dono', {
      ...RESPOSTA,
      equipe: pessoa('Toda a equipe', null, [...vazios, mes(7, 20, 9), mes(8, 0, 0, { parcial: true })]),
      pessoas: [umMes]
    });

    expect(c.series().length).toBe(0);

    const recado = raiz().querySelector('.grafico-equipe .sem-linha-ainda')!.textContent!;
    expect(recado).toContain('Um mês só');
    expect(recado).not.withContext('este caso nao e sobre o mes em andamento').toContain('não fechou');
  });

  /** Sem movimento NENHUM na janela, o cartao do grafico nao aparece — a tabela ja diz que nao
   *  houve nada, e um cartao explicando a ausencia de uma linha seria ruido sobre ruido. */
  it('SEM MOVIMENTO NENHUM, O CARTAO DO GRAFICO NAO APARECE', () => {
    const vazio = [mes(3, 0, 0), mes(4, 0, 0), mes(5, 0, 0),
                   mes(6, 0, 0), mes(7, 0, 0), mes(8, 0, 0, { parcial: true })];

    montar('dono', {
      ...RESPOSTA,
      equipe: pessoa('Toda a equipe', null, vazio),
      pessoas: [pessoa('Sidinaldo', 9, vazio)]
    });

    expect(c.recadoSemLinha()).toBe('');
    expect(raiz().querySelector('.grafico-equipe')).toBeNull();
  });

  // ==================================================================== a escala

  /** ⚠️ UMA ESCALA PARA TODAS AS MINIATURAS. Escala por linha faria quem oscilou entre 29% e 31%
   *  desenhar a mesma montanha de quem foi de 10% a 60% — lado a lado, numa tabela que convida a
   *  comparar. */
  it('AS MINIATURAS COMPARTILHAM UMA ESCALA SÓ', () => {
    montar();

    const todos = [RESPOSTA.equipe!, ANA, BRUNO]
      .flatMap(p => p.meses.map(m => m.conversaoPercentual))
      .filter((v): v is number => v !== null);

    expect(c.escala().minimo).toBe(Math.min(...todos));
    expect(c.escala().maximo).toBe(Math.max(...todos));
  });

  it('SÉRIE QUASE PLANA GANHA FAIXA MÍNIMA, para não virar serra', () => {
    const plano = pessoa('Flat', 11, [
      mes(3, 100, 30), mes(4, 100, 30), mes(5, 100, 31),
      mes(6, 100, 30), mes(7, 100, 31), mes(8, 100, 30, { parcial: true })
    ]);

    montar('dono', { ...RESPOSTA, equipe: null, pessoas: [plano] });

    // 30% a 31% é um ponto de faixa; sem o piso, a miniatura desenharia uma montanha.
    expect(c.escala().maximo - c.escala().minimo).toBeGreaterThanOrEqual(0.09);
  });

  // ==================================================================== a janela

  it('A JANELA TROCA E RECARREGA, pedindo os meses que foram clicados', () => {
    montar();

    const botoes = [...raiz().querySelectorAll('.janelas .aba')] as HTMLButtonElement[];
    expect(botoes.map(b => b.textContent!.trim())).toEqual(['3 meses', '6 meses', '12 meses']);
    expect(botoes[1].getAttribute('aria-pressed')).withContext('6 é o padrão').toBe('true');

    botoes[2].click();
    fixture.detectChanges();

    const req = http.expectOne(r => r.url.endsWith('/evolucao'));
    expect(req.request.params.get('meses')).toBe('12');
    req.flush(RESPOSTA);
    fixture.detectChanges();

    expect(c.janela()).toBe(12);
  });

  it('O RECORTE ESCRITO NO TOPO NOMEIA O MÊS EM ANDAMENTO', () => {
    montar();
    expect(c.periodo()).toBe('mar a ago de 2026 · agosto em andamento');
  });

  // ==================================================================== o detalhe

  it('CLICAR NA PESSOA ABRE O DETALHE NA MESMA TELA, e clicar de novo fecha', () => {
    montar();

    expect(raiz().querySelector('.detalhe')).toBeNull();

    const botao = raiz().querySelectorAll('.abre-pessoa')[0] as HTMLButtonElement;
    expect(botao.textContent!.trim()).toBe('Ana Souza');

    botao.click();
    fixture.detectChanges();

    expect(raiz().querySelector('.detalhe h2')!.textContent!.trim()).toBe('Ana Souza');
    expect(botao.getAttribute('aria-expanded')).toBe('true');

    botao.click();
    fixture.detectChanges();

    expect(raiz().querySelector('.detalhe')).toBeNull();
  });

  /** A régua não abre detalhe: o gráfico dela já é a segunda linha de todo detalhe, e um clique
   *  mostraria a equipe contra ela mesma — uma linha em cima da outra. */
  it('A LINHA DA EQUIPE NÃO É CLICÁVEL', () => {
    montar();

    const primeira = raiz().querySelector('.tabela-evolucao tbody tr')!;
    expect(primeira.querySelector('.abre-pessoa')).toBeNull();
    expect(primeira.querySelector('.nome-equipe')).not.toBeNull();
  });

  // ==================================================================== o vazio e o erro

  it('SEM NINGUÉM, A TELA DIZ O QUE FALTA em vez de mostrar tabela vazia', () => {
    montar('dono', { equipe: null, pessoas: [], de: '2026-03-01', ate: '2026-08-31' });

    expect(raiz().querySelector('.tabela-evolucao')).toBeNull();
    expect(raiz().querySelector('.vazio')!.textContent)
      .toContain('Ninguém fechou nem perdeu negociação');
  });

  it('FALHA DE REDE MOSTRA RECADO, e não uma tela em branco', () => {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting()
      ]
    });

    TestBed.inject(AuthServico).aplicarLogin({
      token: 'tok',
      usuario: {
        id: 1, nome: 'Ana', email: 'a@x.com', papel: 'dono',
        permissoes: PERMISSOES_DE.dono, empresaNome: 'Padaria'
      }
    } as never);

    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(Evolucao);
    c = fixture.componentInstance;
    fixture.detectChanges();

    for (const r of http.match(() => true)) r.error(new ProgressEvent('erro'));
    fixture.detectChanges();

    expect(raiz().querySelector('.erro')!.textContent).toContain('Não foi possível carregar');
  });

  /** O percentual e os meses de casa são os do SERVIDOR (AUD-XX). Aqui eles não batem com os
   *  contadores de propósito (6 de 12 seriam 50%): se a tela voltar a dividir, ou a contar meses
   *  pela data de entrada, ela mostra outra coisa. */
  it('A CONVERSÃO E O TEMPO DE CASA SÃO OS DO SERVIDOR', () => {
    const carla = pessoa('Carla Dias', 9, SEIS, {
      decididos: 12, ganhos: 6, conversaoPercentual: 12, mesesNoNexora: 5,
      noNexoraDesde: '2020-01-01T00:00:00Z'
    });
    montar('dono', { ...RESPOSTA, pessoas: [carla] });

    const linha = [...raiz().querySelectorAll('tr')].find(tr => tr.textContent!.includes('Carla Dias'))!;
    expect(linha.textContent).toContain('12%');
    expect(linha.textContent).not.toContain('50%');
    expect(linha.textContent).toContain('há 5 meses');
  });

  // ==================================================================== tempo de casa

  it('O TEMPO DE CASA RESPONDE "ESTA PESSOA É NOVA?", em meses ou anos', () => {
    montar();

    // Os meses vêm do servidor (AUD-XX); a tela só escolhe a unidade.
    // "há 412 dias" é preciso e não responde a pergunta.
    expect(c.tempoDeCasa(0)).toBe('este mês');
    expect(c.tempoDeCasa(1)).toBe('há 1 mês');
    expect(c.tempoDeCasa(2)).toBe('há 2 meses');
    expect(c.tempoDeCasa(14)).toBe('há 1 ano');
    expect(c.tempoDeCasa(40)).toBe('há 3 anos');
    expect(c.tempoDeCasa(null)).toBe('—');
  });
});
