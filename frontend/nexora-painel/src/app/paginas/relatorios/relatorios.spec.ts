import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { AuthServico } from '../../nucleo/servicos/auth.servico';
import { Relatorios } from './relatorios';
import { PERMISSOES_DE } from '../../nucleo/seguranca/permissoes-de-teste';

/** ===================== RELATÓRIOS (BLOCO 14) =====================
 *
 *  O que estes testes travam não é o desenho da tela — é o que o prompt proíbe explicitamente:
 *
 *   1. rotular como "no período" um dado que é FOTO ATUAL;
 *   2. montar o CSV no browser em vez de buscar do servidor;
 *   3. deixar o vendedor pedir o número de outro vendedor.
 *
 *  O item 3 tem teste de verdade na API (`RelatoriosDbTests`); aqui se prova só que a tela não
 *  OFERECE o caminho — que é cortesia, não proteção, e o comentário do componente diz isso.
 *  ============================================================== */
describe('relatórios (bloco 14)', () => {
  const OPCOES = {
    responsaveis: [{ id: 1, nome: 'Ana' }, { id: 2, nome: 'Bruno' }],
    // ⚠️ DOIS FUNIS, COM NOME DE ETAPA REPETIDO — é a condição do defeito (FUN-1). Com um funil
    //    só, qualquer implementação passa: não há o que distinguir. Os nomes são os de produção,
    //    colidindo só na maiúscula.
    etapas: [
      { id: 10, nome: 'Novo Lead', pipelineId: 1, pipelineNome: 'Vendas' },
      { id: 11, nome: 'Proposta', pipelineId: 1, pipelineNome: 'Vendas' },
      { id: 12, nome: 'Venda', pipelineId: 1, pipelineNome: 'Vendas' },
      { id: 20, nome: 'Novo lead', pipelineId: 2, pipelineNome: 'Atacado' },
      { id: 21, nome: 'Proposta', pipelineId: 2, pipelineNome: 'Atacado' }
    ],
    motivosPerda: ['preço', 'prazo']
  };

  const VENDAS = {
    pontos: [
      { periodo: '2026-08-05', vendas: 2, faturamento: 1000, concluidas: 1, valorConcluido: 400, canceladas: 0, valorCancelado: 0 },
      { periodo: '2026-08-06', vendas: 0, faturamento: 0, concluidas: 0, valorConcluido: 0, canceladas: 1, valorCancelado: 300 }
    ],
    totais: {
      vendas: 2, faturamento: 1000, concluidas: 1, valorConcluido: 400,
      canceladas: 1, valorCancelado: 300, ticketMedio: 500
    }
  };

  // ⚠️ DOIS FUNIS, COM "PROPOSTA" NOS DOIS — a condição do defeito (FUN-1). E note as `ordem`
  //    repetidas (1, 2 em cada): é assim no banco, porque `uq_etapas_ordem` é POR PIPELINE. Uma
  //    carga com ordem única pela empresa esconderia metade do problema.
  const V = (etapaId: number, nome: string, ordem: number) =>
    ({ etapaId, nome, ordem, cor: '#7FA88B', pipelineId: 1, pipelineNome: 'Vendas' });
  const A = (etapaId: number, nome: string, ordem: number) =>
    ({ etapaId, nome, ordem, cor: '#14432F', pipelineId: 2, pipelineNome: 'Atacado' });

  const FUNIL = {
    entradas: [
      { ...V(10, 'Novo Lead', 1), entradas: 42 },
      { ...V(11, 'Proposta', 2), entradas: 19 },
      { ...A(20, 'Novo lead', 1), entradas: 16 },
      { ...A(21, 'Proposta', 2), entradas: 6 }
    ],
    agora: [
      { ...V(10, 'Novo Lead', 1), contatos: 40, valor: 8000 },
      { ...V(11, 'Proposta', 2), contatos: 3, valor: 900 },
      { ...A(20, 'Novo lead', 1), contatos: 12, valor: 4000 },
      { ...A(21, 'Proposta', 2), contatos: 2, valor: 600 }
    ],
    trilhaComecaEm: '2026-08-07T10:00:00Z' as string | null
  };

  let http: HttpTestingController;
  let fixture: ComponentFixture<Relatorios>;
  let c: Relatorios;

  function montar(papel: 'dono' | 'vendedor' = 'dono', opcoes = OPCOES, funil = FUNIL) {
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
      usuario: { id: 1, nome: 'Ana', email: 'a@x.com', papel, permissoes: PERMISSOES_DE[papel], empresaNome: 'X' }
    } as never);

    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(Relatorios);
    c = fixture.componentInstance;
    fixture.detectChanges();

    for (const r of http.match(() => true)) {
      const url = r.request.url;
      if (url.endsWith('/opcoes')) r.flush(opcoes);
      else if (url.endsWith('/vendas')) r.flush(VENDAS);
      else if (url.endsWith('/funil')) r.flush(funil);
      else if (url.endsWith('/recorrentes')) r.flush({ total: 0, numeroPagina: 1, tamanho: 20, itens: [] });
      else r.flush([]);
    }
    fixture.detectChanges();
  }

  afterEach(() => http.verify());

  // ============================================================ FUN-1 · o funil agrupado
  /** ===================== DUAS "PROPOSTA" NA MESMA TELA =====================
   *
   *  O gráfico desenhava as etapas dos dois funis em fila, e a tabela listava as quatro seguidas.
   *  "Proposta" aparecia duas vezes sem nada dizendo de quem era qual.
   *
   *  ⚠️ O SEGUNDO `expect` É O QUE IMPORTA. Só contar os gráficos passaria com um deles levando as
   *  quatro barras e o outro vazio: os grupos existiriam e não agrupariam nada.
   *  ========================================================================= */
  it('desenha UM gráfico por funil, com as barras daquele funil', () => {
    montar();
    const raiz = fixture.nativeElement as HTMLElement;

    expect([...raiz.querySelectorAll('.rotulo-funil')].map(h => h.textContent!.trim()))
      .toEqual(['Vendas', 'Atacado']);

    expect(c.gruposFunilEntradas().map(g => g.barras.map(b => b.rotulo))).toEqual([
      ['Novo Lead', 'Proposta'],
      ['Novo lead', 'Proposta']
    ]);
  });

  /** ===================== UMA ESCALA SÓ ENTRE OS FUNIS =====================
   *  Agrupar não pode custar a comparação. Com cada gráfico se normalizando pelo próprio maior, as
   *  16 entradas do Atacado desenhariam exatamente a mesma barra que as 42 de Vendas — e quem olha
   *  concluiria que os dois trazem o mesmo movimento.
   *
   *  ⚠️ É A DECISÃO OPOSTA À DO PAINEL, e de propósito: lá se lê FORMA e cada funil tem escala
   *  própria; aqui se lê VOLUME. Mesma tela do produto, perguntas diferentes.
   *  ====================================================================== */
  it('todos os gráficos do funil dividem a MESMA escala', () => {
    montar();

    // Escopado ao cartão do funil: a tela tem outros gráficos de barras, e contá-los todos mediria
    // outra coisa.
    const cartao = (fixture.nativeElement as HTMLElement)
      .querySelector('.rotulo-funil')!.closest('section')!;

    const graficos = [...cartao.querySelectorAll('app-grafico-barras')];
    expect(graficos.length).toBe(2);

    // ⚠️ A AFIRMAÇÃO É SOBRE A BARRA DESENHADA, não sobre o `computed`. Checar só
    //    `maximoFunilEntradas()` passava com a entrada `[escalaMaxima]` REMOVIDA do template — a
    //    conta certa existia e não chegava ao gráfico. Sabotagem feita, teste verde, defeito no ar.
    //
    //    42 e 16 são os topos dos dois funis. Sob escala única, a maior barra do Atacado tem de ser
    //    visivelmente menor que a de Vendas; com cada gráfico se normalizando, as duas encostariam
    //    no mesmo teto e a razão abaixo daria 1.
    const maiorDe = (g: Element) => Math.max(...[...g.querySelectorAll('rect.gb-barra')]
      .map(r => Number(r.getAttribute('height'))));

    const razao = maiorDe(graficos[1]) / maiorDe(graficos[0]);

    expect(razao).toBeLessThan(0.6);
    expect(razao).toBeGreaterThan(0.2);   // ~16/42, e não zero por um seletor que não achou nada
  });

  it('a tabela do funil separa as duas "Proposta" por funil', () => {
    montar();
    const raiz = fixture.nativeElement as HTMLElement;

    const linhas = [...raiz.querySelectorAll('.tabela tbody tr')]
      .map(tr => tr.classList.contains('linha-funil')
        ? `== ${tr.textContent!.trim()}`
        : tr.querySelector('td')!.textContent!.trim());

    expect(linhas).toEqual([
      '== Vendas', 'Novo Lead', 'Proposta',
      '== Atacado', 'Novo lead', 'Proposta'
    ]);
  });

  // ============================================================ FUN-1 · o filtro de etapa
  /** ===================== O SELETOR QUE TROCA A RESPOSTA =====================
   *
   *  A etapa escolhida recorta o relatório INTEIRO. Numa lista chata, as duas "Proposta" são
   *  indistinguíveis, e pegar a errada devolve os números do outro processo — sem erro, sem aviso,
   *  e plausíveis demais para alguém conferir.
   *
   *  ⚠️ A AFIRMAÇÃO FORTE É A SEGUNDA. Checar só os rótulos dos grupos passaria com as cinco
   *  etapas despejadas dentro do primeiro `<optgroup>`: o grupo certo existiria e não agruparia
   *  nada. O que prova o conserto é cada "Proposta" estar DEBAIXO do seu próprio funil.
   *  ========================================================================= */
  it('agrupa o filtro de etapa por funil, cada "Proposta" debaixo do seu', () => {
    montar();
    const raiz = fixture.nativeElement as HTMLElement;

    const campo = [...raiz.querySelectorAll('label.campo')]
      .find(l => l.querySelector('span')?.textContent?.trim() === 'Etapa')!;
    const seletor = campo.querySelector('select')!;

    expect([...seletor.querySelectorAll('optgroup')].map(g => g.label))
      .toEqual(['Vendas', 'Atacado']);

    const propostas = [...seletor.querySelectorAll('option')]
      .filter(o => o.textContent!.trim() === 'Proposta');

    expect(propostas.length).toBe(2);
    expect(propostas.map(o => (o.parentElement as HTMLOptGroupElement).label))
      .toEqual(['Vendas', 'Atacado']);
  });

  // ============================================================ o rótulo
  /** ===================== O QUE O PROMPT PROÍBE =====================
   *  "Não rotule como 'no período' um dado que é foto atual."
   *
   *  As duas coisas aparecem na mesma seção, e por isso a separação precisa estar VISÍVEL: dois
   *  títulos e duas colunas com nomes diferentes. Um teste que só checasse os números passaria
   *  com os dois rótulos trocados.
   *  ============================================================== */
  it('separa "entradas no período" de "situação agora", com títulos e colunas distintos', () => {
    montar();
    const raiz = fixture.nativeElement as HTMLElement;

    const titulos = [...raiz.querySelectorAll('.sub-titulo')].map(t => t.textContent!.trim());
    expect(titulos).toContain('Entradas no período');
    expect(titulos).toContain('Situação agora');

    const cabecalhos = [...raiz.querySelectorAll('table th')].map(t => t.textContent!.trim());
    expect(cabecalhos).toContain('Entradas no período');
    expect(cabecalhos).toContain('Contatos agora');

    // E os NÚMEROS não se confundem: 42 entrou em "Novo Lead", 40 está lá agora.
    expect(c.agoraDa(10)?.contatos).toBe(40);
    expect(c.gruposFunilEntradas()[0].barras[0].valor).toBe(42);
  });

  /** A trilha só existe desde o deploy do AUD-1. Sem esta frase na tela, um cliente de um ano vê
   *  zero entradas e conclui que o relatório está quebrado. */
  it('avisa desde quando a movimentação é registrada', () => {
    montar();
    const texto = (fixture.nativeElement as HTMLElement)
      .querySelector('.aviso-trilha')!.textContent!;

    expect(texto).toContain('07/08/2026');
    expect(texto).toContain('não porque nada aconteceu');
  });

  it('sem trilha nenhuma, explica em vez de mostrar zero seco', () => {
    montar('dono', OPCOES, { ...FUNIL, trilhaComecaEm: null });
    const texto = (fixture.nativeElement as HTMLElement)
      .querySelector('.aviso-trilha')!.textContent!;

    expect(texto).toContain('Ainda não há movimentação registrada');
  });

  // ============================================================ cancelado
  /** Cancelado fica FORA do total e aparece assim mesmo — faturamento que some sem rastro é pior
   *  que faturamento errado. O riscado diz na forma o que o rótulo diz em texto. */
  it('mostra o cancelado à parte do faturamento, e riscado', () => {
    montar();
    const raiz = fixture.nativeElement as HTMLElement;

    const rotulos = [...raiz.querySelectorAll('.kpi-rotulo')].map(r => r.textContent!.trim());
    expect(rotulos).toContain('Cancelado (fora do total)');

    const riscado = raiz.querySelector('.kpi-linha .riscado')!;
    expect(riscado.textContent).toContain('300');

    // O faturamento NÃO desconta a cancelada — ela já saiu no servidor.
    expect(c.vendas()!.totais.faturamento).toBe(1000);
  });

  // ============================================================ exportação
  /** ===================== O CSV VEM DO SERVIDOR =====================
   *  "Para volumes grandes, gere no servidor e sirva por endpoint. Não monte CSV de dez mil
   *  linhas no browser."
   *
   *  O teste clica no botão de verdade e confere que saiu UMA requisição, para a rota de CSV, com
   *  `responseType: 'blob'` — o BOM UTF-8 é byte, e lê-lo como texto o transformaria num
   *  caractere invisível no meio do primeiro cabeçalho.
   *  ============================================================== */
  it('o botão de exportar busca o arquivo do servidor, como blob', () => {
    montar();
    const raiz = fixture.nativeElement as HTMLElement;

    const botao = [...raiz.querySelectorAll<HTMLButtonElement>('.link-editar')]
      .find(b => b.textContent!.includes('Exportar CSV'))!;
    botao.click();

    const req = http.expectOne(r => r.url.includes('/relatorios/') && r.url.endsWith('/csv'));
    expect(req.request.method).toBe('GET');
    expect(req.request.responseType).toBe('blob');
    // Os filtros da barra vão junto: exportar o período errado é pior que não exportar.
    expect(req.request.params.get('de')).toBe(c.de());
    expect(req.request.params.get('ate')).toBe(c.ate());

    req.flush(new Blob(['x'], { type: 'text/csv' }));
  });

  // ============================================================ papel
  it('vendedor recebe uma opção só de responsável, e o seletor nasce travado', async () => {
    montar('vendedor', { ...OPCOES, responsaveis: [{ id: 2, nome: 'Bruno' }] });

    // ⚠️ O `await` NÃO é enfeite. `NgModel` faz a própria configuração dentro de um
    // `Promise.resolve().then(...)`, e é lá que o `[disabled]` chega ao elemento. Sem soltar o
    // microtask, o teste lê o estado de antes e falha com a tela correta.
    await Promise.resolve();
    fixture.detectChanges();

    // Ancorado por id, não por posição na grade: reordenar a barra não pode fazer um teste de
    // permissão passar a medir o seletor de origem.
    const select = (fixture.nativeElement as HTMLElement)
      .querySelector<HTMLSelectElement>('#f-responsavel')!;

    expect(c.opcoes().responsaveis.length).toBe(1);
    expect(select.disabled).toBeTrue();
  });

  it('dono escolhe entre os responsáveis da equipe', async () => {
    montar('dono');
    await Promise.resolve();
    fixture.detectChanges();

    const select = (fixture.nativeElement as HTMLElement)
      .querySelector<HTMLSelectElement>('#f-responsavel')!;

    expect(select.disabled).toBeFalse();
    expect(select.querySelectorAll('option').length).toBe(3);   // Todos + Ana + Bruno
  });

  // ============================================================ período
  /** ⚠️ VOLTAR PARA "TODOS" FILTRAVA PELO RESPONSÁVEL 0 — achado em revisão.
   *
   *  O `<select>` usa `[ngValue]`, e com ele o evento já chega TIPADO: `null` para "Todos". O
   *  `(ngModelChange)` fazia `$event === 'null' ? null : +$event` — e `+null` é 0. Escolher Ana e
   *  voltar para Todos deixava o relatório pedindo o responsável de id 0, e ele voltava vazio.
   *
   *  O mesmo padrão estava em quatro telas; o filtro de etiqueta da caixa já tinha sido consertado
   *  antes, o que prova que alguém tropeçou nisso e só aquele ficou certo. */
  it('VOLTAR PARA "TODOS" NÃO FILTRA PELO RESPONSÁVEL 0', async () => {
    montar('dono');
    await Promise.resolve();
    fixture.detectChanges();

    const select = (fixture.nativeElement as HTMLElement)
      .querySelector<HTMLSelectElement>('#f-responsavel')!;

    const escolher = (indice: number) => {
      select.value = select.options[indice].value;
      select.dispatchEvent(new Event('change'));
      fixture.detectChanges();
    };

    escolher(1);
    expect(c.responsavelId()).toBe(1);

    escolher(0);
    expect(c.responsavelId()).withContext('a opção "Todos" virou um id').toBeNull();

    // O que a troca tiver pedido ao servidor é respondido — o `afterEach` confere.
    for (const r of http.match(() => true)) {
      const url = r.request.url;
      if (url.endsWith('/vendas')) r.flush(VENDAS);
      else if (url.endsWith('/funil')) r.flush(FUNIL);
      else if (url.endsWith('/recorrentes')) r.flush({ total: 0, numeroPagina: 1, tamanho: 20, itens: [] });
      else r.flush([]);
    }
  });

  it('período invertido é recusado na tela, sem ida ao servidor', () => {
    montar();
    c.de.set('2026-08-30');
    c.ate.set('2026-08-01');
    c.carregar();

    expect(c.erro()).toContain('não pode ser depois');
    http.expectNone(() => true);
  });

  it('o atalho "mês anterior" cobre o mês inteiro, não até hoje', () => {
    montar();
    c.aplicarAtalho('mes-anterior');

    const de = new Date(c.de() + 'T00:00:00');
    const ate = new Date(c.ate() + 'T00:00:00');

    expect(de.getDate()).withContext('começa no dia 1').toBe(1);
    // Somar um dia ao fim tem que virar o mês: é assim que se prova "último dia" sem repetir a
    // tabela de meses dentro do teste.
    const seguinte = new Date(ate);
    seguinte.setDate(seguinte.getDate() + 1);
    expect(seguinte.getDate()).withContext('termina no último dia do mês').toBe(1);

    for (const r of http.match(() => true)) r.flush({});
  });
});
