import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { AuthServico } from '../../nucleo/servicos/auth.servico';
import { Relatorios } from './relatorios';

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
    etapas: [{ id: 10, nome: 'Novo Lead' }, { id: 11, nome: 'Venda' }],
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

  const FUNIL = {
    entradas: [
      { etapaId: 10, nome: 'Novo Lead', ordem: 1, cor: '#7FA88B', entradas: 7 },
      { etapaId: 11, nome: 'Venda', ordem: 2, cor: '#14432F', entradas: 2 }
    ],
    agora: [
      { etapaId: 10, nome: 'Novo Lead', ordem: 1, cor: '#7FA88B', contatos: 40, valor: 8000 },
      { etapaId: 11, nome: 'Venda', ordem: 2, cor: '#14432F', contatos: 3, valor: 900 }
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
      usuario: { id: 1, nome: 'Ana', email: 'a@x.com', papel, empresaNome: 'X' }
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

    // E os NÚMEROS não se confundem: 7 entrou, 40 está lá.
    expect(c.agoraDa(10)?.contatos).toBe(40);
    expect(c.barrasFunilEntradas()[0].valor).toBe(7);
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
