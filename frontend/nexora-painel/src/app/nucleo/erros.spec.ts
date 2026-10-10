import { erroAo } from './erros';

/** "Não foi possível salvar." não dizia o quê nem o que fazer (BUG-XX, T5). */
describe('a frase de erro', () => {
  it('a frase do servidor vale quando ele explica', () => {
    expect(erroAo({ status: 409, error: { erro: 'Já existe um canal com esse nome.' } }, 'salvar o canal'))
      .toBe('Já existe um canal com esse nome.');
  });

  it('sem resposta, diz o que fazer', () => {
    expect(erroAo({ status: 0, error: null }, 'salvar o canal'))
      .toBe('Não foi possível salvar o canal: o Nexora não respondeu. Confira a internet e tente de novo.');
  });

  it('permissão negada sem frase do servidor diz que é permissão', () => {
    expect(erroAo({ status: 403, error: null }, 'apagar a etapa'))
      .toBe('Você não tem permissão para apagar a etapa.');
  });

  it('o item que sumiu pede para atualizar a página', () => {
    expect(erroAo({ status: 404, error: null }, 'apagar a etapa'))
      .toBe('Não foi possível apagar a etapa: o item não existe mais. Atualize a página.');
  });

  it('servidor fora do ar pede para tentar mais tarde', () => {
    expect(erroAo({ status: 502, error: '<html>Bad Gateway</html>' }, 'salvar o funil'))
      .toBe('Não foi possível salvar o funil agora. Tente de novo em alguns minutos.');
  });

  it('o resto diz o que falhou', () => {
    expect(erroAo({ status: 400, error: { erro: '  ' } }, 'criar a etiqueta'))
      .toBe('Não foi possível criar a etiqueta. Tente de novo.');
    expect(erroAo(new Error('x'), 'criar a etiqueta'))
      .toBe('Não foi possível criar a etiqueta. Tente de novo.');
  });
});
